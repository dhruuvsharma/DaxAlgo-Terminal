using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace DaxAlgo.Strategy.Bundle;

/// <summary>
/// Validates bundle payload shape as metadata only. This is a format and packaging policy check,
/// not a sandbox or a statement that accepted managed code is safe to execute.
/// </summary>
internal static class StrategyBundlePayloadPolicy
{
    private static readonly HashSet<string> ForbiddenNonExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".com", ".msi", ".ps1", ".psm1", ".bat", ".cmd",
        ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".py", ".pyw",
        ".sh", ".bash", ".zsh", ".ksh", ".fish", ".lnk", ".jar", ".scr", ".cpl",
        ".sys",
    };

    private static readonly HashSet<string> ForbiddenEngineAssemblyReferences = new(StringComparer.OrdinalIgnoreCase)
    {
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "System.Xaml",
        "DaxAlgo.Sdk.Wpf",
    };

    public static void Validate(
        string path,
        StrategyBundlePayloadRole role,
        ReadOnlyMemory<byte> content)
    {
        if (role is StrategyBundlePayloadRole.Engine or
            StrategyBundlePayloadRole.WindowsUi or
            StrategyBundlePayloadRole.ManagedDependency)
        {
            ValidateManagedAssembly(path, role, content);
            return;
        }

        var extension = Path.GetExtension(path);
        if (ForbiddenNonExecutableExtensions.Contains(extension))
            Reject(path, $"role '{StrategyBundleManifestCodec.RoleToWire(role)}' may not contain executable or script-style extension '{extension}'.");
        if (LooksLikePortableExecutable(content))
            Reject(path, $"role '{StrategyBundleManifestCodec.RoleToWire(role)}' may not contain Portable Executable content, even under a non-code extension.");
    }

    private static void ValidateManagedAssembly(
        string path,
        StrategyBundlePayloadRole role,
        ReadOnlyMemory<byte> content)
    {
        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata)
                Reject(path, "is not a managed assembly with metadata.");

            var corHeader = pe.PEHeaders.CorHeader;
            if (corHeader is null)
                Reject(path, "has no CLR header and may be native or AOT code.");
            if ((corHeader.Flags & CorFlags.ILOnly) == 0)
                Reject(path, "is not IL-only and may be mixed-mode or native code.");
            if ((corHeader.Flags & CorFlags.NativeEntryPoint) != 0)
                Reject(path, "declares a native entry point.");
            if (corHeader.ManagedNativeHeaderDirectory.RelativeVirtualAddress != 0 ||
                corHeader.ManagedNativeHeaderDirectory.Size != 0)
                Reject(path, "contains a ReadyToRun managed-native header.");

            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly)
                Reject(path, "contains managed metadata but is not an assembly.");

            var definition = metadata.GetAssemblyDefinition();
            var identity = metadata.GetString(definition.Name);
            if (IsHostAssemblyIdentity(identity))
                Reject(path, $"bundles host-owned assembly identity '{identity}'.");

            if (role is StrategyBundlePayloadRole.Engine or StrategyBundlePayloadRole.ManagedDependency)
                ValidateEngineSafeReferences(path, role, metadata);
        }
        catch (StrategyBundleValidationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException or ArgumentException or OverflowException)
        {
            throw new StrategyBundleValidationException(
                StrategyBundleValidationError.InvalidPayloadSet,
                $"Payload '{path}' is not a valid managed IL-only assembly: {ex.Message}",
                ex);
        }
    }

    public static void ValidateManagedAssemblyIdentities(IReadOnlyDictionary<string, byte[]> payloads)
    {
        _ = DescribeManagedAssemblies(payloads);
    }

    public static IReadOnlyList<StrategyBundleManagedAssemblyDescriptor> DescribeManagedAssemblies(
        IReadOnlyDictionary<string, byte[]> payloads)
    {
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metadataEntries = new List<ManagedAssemblyMetadata>();
        foreach (var payload in payloads.OrderBy(static payload => payload.Key, StringComparer.Ordinal))
        {
            if (!IsManagedPayloadPath(payload.Key))
                continue;

            var metadata = ReadManagedAssemblyMetadata(payload.Key, payload.Value);
            var descriptor = metadata.Descriptor;
            if (!identities.TryAdd(descriptor.Name, payload.Key))
                Reject(
                    payload.Key,
                    $"duplicates managed assembly identity '{descriptor.Name}' from payload '{identities[descriptor.Name]}'.");
            metadataEntries.Add(metadata);
        }

        ValidateManagedDependencyClosure(metadataEntries);
        return metadataEntries.Select(static entry => entry.Descriptor).ToArray();
    }

    private static void ValidateManagedDependencyClosure(
        IReadOnlyList<ManagedAssemblyMetadata> assemblies)
    {
        var bundledByName = assemblies.ToDictionary(
            static assembly => assembly.Definition.Name,
            StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in assemblies)
        {
            foreach (var reference in assembly.References)
            {
                if (bundledByName.TryGetValue(reference.Name, out var dependency))
                {
                    if (!AssemblyIdentityMatches(reference, dependency.Definition))
                    {
                        RejectGraph(
                            $"entry '{assembly.Descriptor.Path}' requests '{FormatIdentity(reference)}', " +
                            $"but bundled payload '{dependency.Descriptor.Path}' defines '{FormatIdentity(dependency.Definition)}'.");
                    }
                    continue;
                }
                if (StrategyBundleExternalAssemblyPolicy.IsAllowed(reference.Name)) continue;

                RejectGraph(
                    $"entry '{assembly.Descriptor.Path}' references private assembly '{reference.Name}', but that assembly is not bundled.");
            }
        }
    }

    public static void ValidateManagedAssemblyGraph(
        IReadOnlyList<StrategyBundleManagedAssemblyDescriptor> declared,
        IReadOnlyDictionary<string, byte[]> payloads)
    {
        var actual = DescribeManagedAssemblies(payloads);
        if (declared.Count != actual.Count)
            RejectGraph("does not contain exactly one descriptor for every managed payload.");
        for (var index = 0; index < actual.Count; index++)
        {
            var expected = declared[index];
            var observed = actual[index];
            if (!string.Equals(expected.Path, observed.Path, StringComparison.Ordinal) ||
                !string.Equals(expected.Name, observed.Name, StringComparison.Ordinal) ||
                !expected.References.SequenceEqual(observed.References, StringComparer.Ordinal))
            {
                RejectGraph($"entry '{expected.Path}' does not match its managed PE metadata.");
            }
        }
    }

    public static IReadOnlyList<StrategyBundleEngineAssemblyDescriptor> ResolveEngineClosure(
        StrategyBundleManifest manifest)
    {
        if (manifest.Engine is null)
            RejectGraph("has no engine entry point.");

        var payloadsByPath = new Dictionary<string, StrategyBundlePayloadDescriptor>(StringComparer.Ordinal);
        foreach (var payload in manifest.Payloads ?? [])
        {
            if (payload is null)
                RejectGraph("contains a null payload descriptor.");
            if (!payloadsByPath.TryAdd(payload.Path, payload))
                RejectGraph($"contains duplicate payload path '{payload.Path}'.");
        }

        var assembliesByPath = new Dictionary<string, StrategyBundleManagedAssemblyDescriptor>(StringComparer.Ordinal);
        var assembliesByName = new Dictionary<string, StrategyBundleManagedAssemblyDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in manifest.ManagedAssemblies ?? [])
        {
            if (assembly is null)
                RejectGraph("contains a null managed assembly descriptor.");
            if (!assembliesByPath.TryAdd(assembly.Path, assembly))
                RejectGraph($"contains duplicate managed assembly path '{assembly.Path}'.");
            if (!assembliesByName.TryAdd(assembly.Name, assembly))
                RejectGraph($"contains duplicate managed assembly identity '{assembly.Name}'.");
            if (!payloadsByPath.ContainsKey(assembly.Path))
                RejectGraph($"entry '{assembly.Path}' has no matching payload descriptor.");
        }

        if (!assembliesByPath.TryGetValue(manifest.Engine.AssemblyPath, out var engine))
            RejectGraph($"has no managed assembly descriptor for engine '{manifest.Engine.AssemblyPath}'.");

        var enginePayload = payloadsByPath[engine.Path];
        if (enginePayload.Role != StrategyBundlePayloadRole.Engine)
            RejectGraph($"engine entry '{engine.Path}' has invalid role '{RoleName(enginePayload.Role)}'.");

        var reachable = new Dictionary<string, StrategyBundleManagedAssemblyDescriptor>(StringComparer.Ordinal);
        var pending = new Stack<StrategyBundleManagedAssemblyDescriptor>();
        pending.Push(engine);
        while (pending.Count > 0)
        {
            var assembly = pending.Pop();
            if (!reachable.TryAdd(assembly.Path, assembly))
                continue;

            foreach (var reference in (assembly.References ?? [])
                         .Distinct(StringComparer.Ordinal)
                         .OrderByDescending(static value => value, StringComparer.Ordinal))
            {
                if (!assembliesByName.TryGetValue(reference, out var dependency))
                {
                    if (StrategyBundleExternalAssemblyPolicy.IsAllowed(reference))
                        continue;
                    RejectGraph(
                        $"entry '{assembly.Path}' references private assembly '{reference}', but that assembly is not bundled.");
                }

                var payload = payloadsByPath[dependency.Path];
                if (payload.Role == StrategyBundlePayloadRole.WindowsUi)
                {
                    RejectGraph(
                        $"engine closure reaches Windows UI assembly '{dependency.Name}' at '{dependency.Path}' from '{assembly.Path}'.");
                }
                if (payload.Role != StrategyBundlePayloadRole.ManagedDependency &&
                    !string.Equals(dependency.Path, engine.Path, StringComparison.Ordinal))
                {
                    RejectGraph(
                        $"engine closure reaches assembly '{dependency.Name}' at '{dependency.Path}' with invalid role '{RoleName(payload.Role)}'.");
                }

                pending.Push(dependency);
            }
        }

        var ordered = reachable.Values
            .Where(assembly => !string.Equals(assembly.Path, engine.Path, StringComparison.Ordinal))
            .OrderBy(static assembly => assembly.Path, StringComparer.Ordinal)
            .Prepend(engine);
        return ordered
            .Select(assembly =>
            {
                var payload = payloadsByPath[assembly.Path];
                var references = (assembly.References ?? [])
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static value => value, StringComparer.Ordinal)
                    .ToArray();
                return new StrategyBundleEngineAssemblyDescriptor(
                    assembly.Path,
                    payload.Role,
                    assembly.Name,
                    references,
                    payload.Length,
                    payload.Sha256);
            })
            .ToArray();
    }

    private static string RoleName(StrategyBundlePayloadRole role) =>
        Enum.IsDefined(role)
            ? StrategyBundleManifestCodec.RoleToWire(role)
            : ((int)role).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void ValidateEngineSafeReferences(
        string path,
        StrategyBundlePayloadRole role,
        MetadataReader metadata)
    {
        var roleName = StrategyBundleManifestCodec.RoleToWire(role);
        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            var name = metadata.GetString(reference.Name);
            if (IsForbiddenEngineAssemblyReference(name))
                Reject(path, $"role '{roleName}' references UI/WPF assembly '{name}'.");
        }

        foreach (var handle in metadata.TypeReferences)
        {
            var reference = metadata.GetTypeReference(handle);
            var namespaceName = reference.Namespace.IsNil
                ? string.Empty
                : metadata.GetString(reference.Namespace);
            if (IsForbiddenEngineNamespace(namespaceName))
                Reject(path, $"role '{roleName}' references UI/WPF namespace '{namespaceName}'.");
        }
    }

    private static ManagedAssemblyMetadata ReadManagedAssemblyMetadata(
        string path,
        byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var metadata = pe.GetMetadataReader();
            var definition = ReadIdentity(metadata, metadata.GetAssemblyDefinition());
            var references = metadata.AssemblyReferences
                .Select(metadata.GetAssemblyReference)
                .Select(reference => ReadIdentity(metadata, reference))
                .OrderBy(static reference => reference.Name, StringComparer.Ordinal)
                .ThenBy(static reference => reference.Version)
                .ThenBy(static reference => reference.PublicKeyToken, StringComparer.Ordinal)
                .ToArray();
            var referenceNames = references
                .Select(static reference => reference.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static reference => reference, StringComparer.Ordinal)
                .ToArray();
            return new ManagedAssemblyMetadata(
                new StrategyBundleManagedAssemblyDescriptor(path, definition.Name, referenceNames),
                definition,
                references);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException or ArgumentException or OverflowException)
        {
            throw new StrategyBundleValidationException(
                StrategyBundleValidationError.InvalidPayloadSet,
                $"Payload '{path}' has unreadable managed assembly identity metadata: {ex.Message}",
                ex);
        }
    }

    private static ManagedAssemblyIdentity ReadIdentity(
        MetadataReader metadata,
        AssemblyDefinition definition) => new(
        metadata.GetString(definition.Name),
        definition.Version,
        definition.Culture.IsNil ? string.Empty : metadata.GetString(definition.Culture),
        PublicKeyToken(
            definition.PublicKey.IsNil ? [] : metadata.GetBlobBytes(definition.PublicKey),
            (definition.Flags & AssemblyFlags.PublicKey) != 0),
        (definition.Flags & AssemblyFlags.WindowsRuntime) != 0,
        (definition.Flags & AssemblyFlags.Retargetable) != 0);

    private static ManagedAssemblyIdentity ReadIdentity(
        MetadataReader metadata,
        AssemblyReference reference) => new(
        metadata.GetString(reference.Name),
        reference.Version,
        reference.Culture.IsNil ? string.Empty : metadata.GetString(reference.Culture),
        PublicKeyToken(
            reference.PublicKeyOrToken.IsNil ? [] : metadata.GetBlobBytes(reference.PublicKeyOrToken),
            (reference.Flags & AssemblyFlags.PublicKey) != 0),
        (reference.Flags & AssemblyFlags.WindowsRuntime) != 0,
        (reference.Flags & AssemblyFlags.Retargetable) != 0);

    private static string PublicKeyToken(byte[] keyOrToken, bool containsPublicKey)
    {
        if (keyOrToken.Length == 0) return string.Empty;
        if (!containsPublicKey) return Convert.ToHexStringLower(keyOrToken);
        var hash = SHA1.HashData(keyOrToken);
        Span<byte> token = stackalloc byte[8];
        for (var index = 0; index < token.Length; index++) token[index] = hash[hash.Length - 1 - index];
        return Convert.ToHexStringLower(token);
    }

    private static bool AssemblyIdentityMatches(
        ManagedAssemblyIdentity requested,
        ManagedAssemblyIdentity resolved) =>
        string.Equals(requested.Name, resolved.Name, StringComparison.OrdinalIgnoreCase) &&
        requested.Version == resolved.Version &&
        string.Equals(requested.Culture, resolved.Culture, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(requested.PublicKeyToken, resolved.PublicKeyToken, StringComparison.Ordinal) &&
        requested.IsWindowsRuntime == resolved.IsWindowsRuntime &&
        requested.IsRetargetable == resolved.IsRetargetable;

    private static string FormatIdentity(ManagedAssemblyIdentity identity) =>
        $"{identity.Name}, Version={identity.Version}, Culture=" +
        $"{(identity.Culture.Length == 0 ? "neutral" : identity.Culture)}, PublicKeyToken=" +
        $"{(identity.PublicKeyToken.Length == 0 ? "null" : identity.PublicKeyToken)}";

    private sealed record ManagedAssemblyMetadata(
        StrategyBundleManagedAssemblyDescriptor Descriptor,
        ManagedAssemblyIdentity Definition,
        IReadOnlyList<ManagedAssemblyIdentity> References);

    private sealed record ManagedAssemblyIdentity(
        string Name,
        Version Version,
        string Culture,
        string PublicKeyToken,
        bool IsWindowsRuntime,
        bool IsRetargetable);

    private static bool IsManagedPayloadPath(string path) =>
        path.StartsWith("payload/engine/", StringComparison.Ordinal) ||
        path.StartsWith("payload/windows/", StringComparison.Ordinal) ||
        path.StartsWith("payload/deps/", StringComparison.Ordinal);

    private static bool LooksLikePortableExecutable(ReadOnlyMemory<byte> content)
    {
        var bytes = content.Span;
        if (bytes.Length >= 2 && bytes[0] == (byte)'M' && bytes[1] == (byte)'Z')
            return true;

        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            return pe.PEHeaders.PEHeader is not null || pe.HasMetadata;
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static bool IsHostAssemblyIdentity(string name) =>
        StrategyBundleExternalAssemblyPolicy.IsAllowed(name);

    internal static bool IsForbiddenEngineAssemblyReference(string name) =>
        ForbiddenEngineAssemblyReferences.Contains(name) ||
        string.Equals(name, "Accessibility", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Microsoft.VisualBasic.Forms", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "PresentationUI", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "System.Printing", StringComparison.OrdinalIgnoreCase) ||
        IsAssemblyNameOrChild(name, "System.Windows") ||
        IsAssemblyNameOrChild(name, "PresentationFramework") ||
        IsAssemblyNameOrChild(name, "System.Windows.Forms") ||
        IsAssemblyNameOrChild(name, "WindowsFormsIntegration") ||
        IsAssemblyNameOrChild(name, "ReachFramework") ||
        IsAssemblyNameOrChild(name, "UIAutomation") ||
        IsAssemblyNameOrChild(name, "TradingTerminal.UI") ||
        IsAssemblyNameOrChild(name, "MahApps") ||
        IsAssemblyNameOrChild(name, "ControlzEx") ||
        IsAssemblyNameOrChild(name, "Microsoft.Xaml.Behaviors") ||
        IsAssemblyNameOrChild(name, "Microsoft.UI") ||
        IsAssemblyNameOrChild(name, "Avalonia") ||
        IsAssemblyNameOrChild(name, "ICSharpCode.AvalonEdit") ||
        IsAssemblyNameOrChild(name, "ScottPlot") ||
        IsAssemblyNameOrChild(name, "SkiaSharp.Views") ||
        IsAssemblyNameOrChild(name, "CommunityToolkit.Mvvm");

    private static bool IsForbiddenEngineNamespace(string name) =>
        IsNamespaceOrChild(name, "System.Windows") ||
        IsNamespaceOrChild(name, "System.Windows.Forms") ||
        IsNamespaceOrChild(name, "DaxAlgo.Sdk.Wpf") ||
        IsNamespaceOrChild(name, "TradingTerminal.UI") ||
        IsNamespaceOrChild(name, "MahApps") ||
        IsNamespaceOrChild(name, "ControlzEx") ||
        IsNamespaceOrChild(name, "Microsoft.Xaml.Behaviors") ||
        IsNamespaceOrChild(name, "Microsoft.UI") ||
        IsNamespaceOrChild(name, "Avalonia") ||
        IsNamespaceOrChild(name, "ICSharpCode.AvalonEdit") ||
        IsNamespaceOrChild(name, "ScottPlot") ||
        IsNamespaceOrChild(name, "SkiaSharp.Views") ||
        IsNamespaceOrChild(name, "CommunityToolkit.Mvvm");

    private static bool IsAssemblyNameOrChild(string value, string root) =>
        string.Equals(value, root, StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith(root + ".", StringComparison.OrdinalIgnoreCase);

    private static bool IsNamespaceOrChild(string value, string root) =>
        string.Equals(value, root, StringComparison.Ordinal) ||
        value.StartsWith(root + ".", StringComparison.Ordinal);

    [DoesNotReturn]
    private static void Reject(string path, string reason) =>
        throw new StrategyBundleValidationException(
            StrategyBundleValidationError.InvalidPayloadSet,
            $"Payload '{path}' {reason}");

    [DoesNotReturn]
    private static void RejectGraph(string reason) =>
        throw new StrategyBundleValidationException(
            StrategyBundleValidationError.InvalidPayloadSet,
            $"Managed assembly graph {reason}");
}
