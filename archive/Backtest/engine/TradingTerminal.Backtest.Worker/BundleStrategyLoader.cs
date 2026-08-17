using System.Security.Cryptography;
using DaxAlgo.Sdk;
using DaxAlgo.Strategy.Bundle;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Backtest.Protocol;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;

namespace TradingTerminal.Backtest.Worker;

internal sealed class BundleStrategyExecution(
    BacktestStrategyKernelAdapter kernel,
    BundleStrategyLoadContext loadContext,
    string strategyAssemblySha256,
    IReadOnlyList<BacktestLoadedAssemblyFingerprint> closure) : IAsyncDisposable
{
    private int _disposed;

    public BacktestStrategyKernelAdapter Kernel { get; } = kernel;
    public string StrategyAssemblySha256 { get; } = strategyAssemblySha256;
    public IReadOnlyList<BacktestLoadedAssemblyFingerprint> Closure { get; } = closure;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await Kernel.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            loadContext.ClearAndUnload();
        }
    }
}

internal static class BundleStrategyLoader
{
    public static async Task<BundleStrategyExecution> LoadAsync(
        string jobDirectory,
        BacktestJobRequest request)
    {
        var bundle = request.Strategy.InstalledBundle
                     ?? throw new BacktestProtocolException(
                         "missing_bundle_reference",
                         "The installed bundle reference is missing.");
        if (!request.Run.Universe.IsSingleInstrument)
            throw new BacktestProtocolException(
                "unsupported_bundle_universe",
                "Packaged strategy factories currently support exactly one instrument per run.");

        var root = Path.Combine(jobDirectory, BacktestJobFiles.StrategyDirectory);
        EnsureDirectory(root, "missing_strategy_image");
        var manifestPath = ResolveContainedPath(root, BacktestJobFiles.StrategyManifest);
        var manifestBytes = ReadBoundedFile(
            manifestPath,
            StrategyBundleLimitOptions.Default.MaxManifestBytes,
            "strategy_manifest_limit");
        var manifest = StrategyBundleRuntimePolicy.ParseCanonicalManifest(manifestBytes);
        var contentRoot = StrategyBundleRuntimePolicy.ComputeContentRoot(manifestBytes);
        RequireEqual(contentRoot, bundle.ContentRootSha256, "bundle_content_root_mismatch",
            "The staged canonical manifest does not match the requested content root.");
        RequireEqual(manifest.Identity.Id, request.Strategy.Id, "bundle_strategy_id_mismatch",
            "The staged manifest names a different strategy.");
        RequireEqual(manifest.Identity.PublisherId, bundle.PublisherId, "bundle_publisher_mismatch",
            "The staged manifest names a different publisher.");
        RequireEqual(manifest.Identity.Version, bundle.StrategyVersion, "bundle_version_mismatch",
            "The staged manifest names a different strategy version.");
        RequireEqual(manifest.Compatibility.TargetSdkVersion, request.SdkVersion, "bundle_sdk_mismatch",
            "The staged bundle targets a different SDK version.");
        RequireEqual(manifest.Engine.Contract, StrategyBundleEngineEntryPoint.CurrentContract,
            "bundle_factory_contract_mismatch", "The staged bundle uses an unsupported factory contract.");
        RequireEqual(manifest.Engine.Activation, StrategyBundleEngineEntryPoint.CurrentActivation,
            "bundle_factory_activation_mismatch", "The staged bundle uses an unsupported factory activation.");

        var expectedClosure = DaxStrategyBundle.ResolveEngineClosure(manifest);
        var expectedFiles = expectedClosure
            .Select(static assembly => assembly.Path)
            .Append(BacktestJobFiles.StrategyManifest)
            .ToHashSet(StringComparer.Ordinal);
        ValidateExactFileSet(root, expectedFiles);

        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var assembly in expectedClosure)
        {
            var path = ResolveContainedPath(root, assembly.Path);
            payloads.Add(assembly.Path, ReadBoundedFile(path, assembly.Length, "strategy_assembly_limit"));
        }
        var closure = StrategyBundleRuntimePolicy.ValidateEngineImage(manifest, payloads);
        ValidateWorkerDependencyClosure(closure);
        var engineDescriptor = closure[0];
        RequireEqual(engineDescriptor.Sha256, request.Strategy.ExpectedAssemblySha256!,
            "strategy_assembly_hash_mismatch",
            "The verified bundle engine does not match Strategy.ExpectedAssemblySha256.");

        var privateAssemblies = closure
            .Skip(1)
            .ToDictionary(
                static assembly => assembly.Name,
                assembly => payloads[assembly.Path],
                StringComparer.OrdinalIgnoreCase);
        var loadContext = new BundleStrategyLoadContext(privateAssemblies);
        try
        {
            var engineAssembly = loadContext.LoadEngine(payloads[manifest.Engine.AssemblyPath]);
            var type = engineAssembly.GetType(manifest.Engine.TypeName, throwOnError: false, ignoreCase: false)
                       ?? throw new BacktestProtocolException(
                           "bundle_factory_type_missing",
                           $"Factory type '{manifest.Engine.TypeName}' was not found in the engine assembly.");
            if (!ReferenceEquals(type.Assembly, engineAssembly) ||
                !type.IsClass || type.IsAbstract || !type.IsPublic ||
                !typeof(IStrategyEngineFactory).IsAssignableFrom(type) ||
                type.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new BacktestProtocolException(
                    "invalid_bundle_factory_type",
                    $"Factory type '{manifest.Engine.TypeName}' does not satisfy the packaged strategy contract.");
            }

            IStrategyEngineFactory factory;
            try
            {
                factory = (IStrategyEngineFactory)(Activator.CreateInstance(type)
                          ?? throw new InvalidOperationException("The strategy factory constructor returned null."));
            }
            catch (Exception ex) when (ex is not BacktestProtocolException)
            {
                throw PluginFailure("bundle_factory_construction_failed", "Strategy factory construction failed", ex);
            }

            IBacktestStrategy strategy;
            try
            {
                ValidateDataRequirement(factory.DataRequirement);
                var parameters = CreateParameters(factory.Schema, request.Strategy.ActivationParameters);
                strategy = factory.Create(request.Run.Universe.Primary.Contract, parameters)
                           ?? throw new InvalidOperationException("The strategy factory returned null.");
            }
            catch (Exception ex) when (ex is not BacktestProtocolException)
            {
                throw PluginFailure("bundle_strategy_activation_failed", "Strategy activation failed", ex);
            }
            finally
            {
                if (factory is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else if (factory is IDisposable disposable)
                    disposable.Dispose();
            }

            return new BundleStrategyExecution(
                new BacktestStrategyKernelAdapter(strategy),
                loadContext,
                engineDescriptor.Sha256,
                closure.Select(static assembly =>
                        new BacktestLoadedAssemblyFingerprint(assembly.Name, assembly.Sha256))
                    .ToArray());
        }
        catch
        {
            loadContext.ClearAndUnload();
            throw;
        }
        finally
        {
            payloads.Clear();
        }
    }

    private static StrategyParameters CreateParameters(
        StrategyParameterSchema schema,
        IReadOnlyList<BacktestStrategyParameter> supplied)
    {
        if (schema is null)
            throw new BacktestProtocolException("missing_bundle_parameter_schema", "The strategy factory returned no parameter schema.");
        if (schema.Parameters.Count != supplied.Count)
            throw new BacktestProtocolException(
                "bundle_parameter_set_mismatch",
                "The activation parameter keys do not exactly match the strategy schema.");

        var suppliedByKey = supplied.ToDictionary(static parameter => parameter.Key, StringComparer.Ordinal);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var parameter in schema.Parameters)
        {
            if (!suppliedByKey.TryGetValue(parameter.Key, out var value))
                throw new BacktestProtocolException(
                    "bundle_parameter_set_mismatch",
                    $"The activation parameter '{parameter.Key}' is missing.");
            values.Add(parameter.Key, ValidateParameter(parameter, value));
        }
        return new StrategyParameters(schema, values);
    }

    private static object ValidateParameter(
        StrategyParameter schema,
        BacktestStrategyParameter supplied)
    {
        switch (schema.Kind)
        {
            case ParameterKind.Integer when supplied.Kind == BacktestStrategyParameterKind.Integer:
            {
                var value = supplied.IntegerValue!.Value;
                ValidateBounds(schema, value);
                return value;
            }
            case ParameterKind.Number when supplied.Kind == BacktestStrategyParameterKind.Number:
            {
                var value = supplied.NumberValue!.Value;
                ValidateBounds(schema, value);
                return value;
            }
            case ParameterKind.Boolean when supplied.Kind == BacktestStrategyParameterKind.Boolean:
                return supplied.BooleanValue!.Value;
            case ParameterKind.Choice when supplied.Kind == BacktestStrategyParameterKind.Choice:
            {
                var value = supplied.StringValue!;
                if (schema.Choices is null || !schema.Choices.Contains(value, StringComparer.Ordinal))
                    throw new BacktestProtocolException(
                        "bundle_parameter_out_of_range",
                        $"Activation parameter '{schema.Key}' is not an allowed choice.");
                return value;
            }
            case ParameterKind.Text when supplied.Kind == BacktestStrategyParameterKind.Text:
                return supplied.StringValue!;
            default:
                throw new BacktestProtocolException(
                    "bundle_parameter_kind_mismatch",
                    $"Activation parameter '{schema.Key}' does not match its declared kind.");
        }
    }

    private static void ValidateBounds(StrategyParameter parameter, double value)
    {
        if (!double.IsFinite(value) ||
            parameter.Min is { } min && value < min ||
            parameter.Max is { } max && value > max)
        {
            throw new BacktestProtocolException(
                "bundle_parameter_out_of_range",
                $"Activation parameter '{parameter.Key}' is outside its declared bounds.");
        }
    }

    private static void ValidateDataRequirement(StrategyDataRequirement requirement)
    {
        const StrategyDataRequirement supported = StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;
        if ((requirement & ~supported) != 0)
            throw new BacktestProtocolException(
                "unsupported_bundle_data_requirement",
                $"The worker input does not satisfy data requirement '{requirement}'.");
    }

    private static void ValidateWorkerDependencyClosure(
        IReadOnlyList<StrategyBundleEngineAssemblyDescriptor> closure)
    {
        var bundledNames = closure
            .Select(static assembly => assembly.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in closure)
        {
            foreach (var reference in assembly.References)
            {
                if (bundledNames.Contains(reference) ||
                    BundleStrategyLoadContext.IsWorkerExternalAssemblyAvailable(reference))
                {
                    continue;
                }
                throw new BacktestProtocolException(
                    "bundle_dependency_unavailable_in_worker",
                    $"Strategy assembly '{assembly.Name}' references '{reference}', which is not bundled or supplied by the worker runtime.");
            }
        }
    }

    private static void ValidateExactFileSet(string root, IReadOnlySet<string> expected)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var observedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var expectedDirectories = RequiredDirectories(expected);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new BacktestProtocolException(
                        "strategy_image_reparse_point",
                        "The staged strategy image cannot contain reparse points or device files.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var relativeDirectory = Path.GetRelativePath(root, path)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    if (!observedDirectories.Add(relativeDirectory) ||
                        !expectedDirectories.Contains(relativeDirectory))
                    {
                        throw new BacktestProtocolException(
                            "unexpected_strategy_image_directory",
                            $"The staged strategy image contains unexpected directory '{relativeDirectory}'.");
                    }
                    pending.Push(path);
                    continue;
                }
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (!observed.Add(relative) || !expected.Contains(relative))
                    throw new BacktestProtocolException(
                        "unexpected_strategy_image_file",
                        $"The staged strategy image contains unexpected file '{relative}'.");
            }
        }
        if (!observed.SetEquals(expected) || !observedDirectories.SetEquals(expectedDirectories))
            throw new BacktestProtocolException(
                "incomplete_strategy_image",
                "The staged strategy image is missing a required engine-closure file.");
    }

    private static IReadOnlySet<string> RequiredDirectories(IEnumerable<string> files)
    {
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var segments = file.Split('/');
            for (var count = 1; count < segments.Length; count++)
                directories.Add(string.Join('/', segments.Take(count)));
        }
        return directories;
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath))
            throw new BacktestProtocolException("invalid_strategy_image_path", "Strategy image paths must be relative.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BacktestProtocolException("invalid_strategy_image_path", "A strategy image path escaped its root.");
        return candidate;
    }

    private static byte[] ReadBoundedFile(string path, long maximumLength, string errorCode)
    {
        EnsureFile(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length is < 0 or > int.MaxValue || stream.Length > maximumLength)
            throw new BacktestProtocolException(errorCode, $"Strategy image file '{Path.GetFileName(path)}' exceeds its limit.");
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void EnsureDirectory(string path, string errorCode)
    {
        if (!Directory.Exists(path))
            throw new BacktestProtocolException(errorCode, "The staged strategy image directory is missing.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new BacktestProtocolException("strategy_image_reparse_point", "The staged strategy image root is a reparse point.");
    }

    private static void EnsureFile(string path)
    {
        if (!File.Exists(path))
            throw new BacktestProtocolException("missing_strategy_image_file", $"Strategy image file '{path}' is missing.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new BacktestProtocolException("strategy_image_reparse_point", "The staged strategy image cannot contain reparse points.");
    }

    private static void RequireEqual(string actual, string expected, string code, string message)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new BacktestProtocolException(code, message);
    }

    private static BacktestProtocolException PluginFailure(string code, string prefix, Exception error)
    {
        var detail = error.GetBaseException().Message;
        if (detail.Length > 512) detail = detail[..512];
        return new BacktestProtocolException(code, $"{prefix}: {detail}");
    }
}
