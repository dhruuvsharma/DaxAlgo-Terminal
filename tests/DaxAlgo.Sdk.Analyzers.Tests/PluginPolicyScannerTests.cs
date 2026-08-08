using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using DaxAlgo.Sdk;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Plugins;
using Xunit;

namespace DaxAlgo.Sdk.Analyzers.Tests;

/// <summary>
/// Behavioral agreement tests for the source analyzer and the no-execute IL scanner. The fixtures
/// are real Roslyn-emitted assemblies; the scanner receives their bytes without loading them.
/// </summary>
public sealed class PluginPolicyScannerTests
{
    private static readonly Lazy<MetadataReference[]> RuntimeReferences = new(BuildReferences);

    private static readonly Lazy<PortableExecutableReference> SyntheticHostReference = new(() =>
        MetadataReference.CreateFromImage(ImmutableArray.Create<byte>(Compile("""
            namespace TradingTerminal.Infrastructure.SandboxProbe { public sealed class Marker { } }
            namespace TradingTerminal.MarketData.SandboxProbe { public sealed class Marker { } }
            namespace TradingTerminal.Backtest.SandboxProbe { public sealed class Marker { } }
            namespace TradingTerminal.App.SandboxProbe { public sealed class Marker { } }
            namespace TradingTerminal.Execution.SandboxProbe { public sealed class Marker { } }
            namespace TradingTerminal.UI.SandboxProbe { public sealed class Marker { } }
            namespace TradingTerminal.Login.SandboxProbe { public sealed class Marker { } }
            namespace TradingTerminal.Settings.SandboxProbe { public sealed class Marker { } }
            namespace TradingTerminal.Recording.SandboxProbe { public sealed class Marker { } }
            namespace TradingTerminal.Core.Brokers.SandboxProbe { public sealed class Marker { } }
            namespace TradingTerminal.Core.Trading.SandboxProbe { public sealed class Marker { } }
            namespace TradingTerminal.Core.Backtest.SandboxProbe { public sealed class Marker { } }
            namespace TradingTerminal.Core.Backtesting.SandboxProbe { public sealed class Marker { } }

            namespace TradingTerminal.Extensions
            {
                public sealed class SecretStore { }
                public sealed class SecretRepository { }
                public sealed class SecretBrokerClient { }
                public sealed class SecretBrokerSelector { }
            }
            """, assemblyName: "SyntheticHostBoundary"))));

    public static IEnumerable<object[]> ForbiddenCases()
    {
        yield return Case("System.IO.File", FileIoSource, "fileIo", PluginScanSeverity.Warn);
        yield return Case(
            "System.IO.Path",
            "public static class Fixture { public static string Use() => System.IO.Path.Combine(\"a\", \"b\"); }",
            "fileIo",
            PluginScanSeverity.Clean);
        yield return Case(
            "System.IO subnamespace",
            "public static class Fixture { public static System.Type Use() => typeof(System.IO.Compression.ZipArchive); }",
            "fileIo",
            PluginScanSeverity.Clean);
        yield return Case("System.Net.Http", NetworkSource, "network", PluginScanSeverity.Warn);
        yield return Case(
            "System.Net.Dns",
            "public static class Fixture { public static string Use() => System.Net.Dns.GetHostName(); }",
            "network",
            PluginScanSeverity.Clean);
        yield return Case(
            "System.Diagnostics.Process",
            "public static class Fixture { public static System.Type Use() => typeof(System.Diagnostics.Process); }",
            "process",
            PluginScanSeverity.Block);
        yield return Case(
            "System.Diagnostics.ProcessStartInfo",
            "public static class Fixture { public static System.Type Use() => typeof(System.Diagnostics.ProcessStartInfo); }",
            "process",
            PluginScanSeverity.Block);
        yield return Case(
            "Microsoft.Win32.Registry",
            "public static class Fixture { public static object? Use() => Microsoft.Win32.Registry.CurrentUser; }",
            "registry",
            PluginScanSeverity.Block);
        yield return Case(
            "Microsoft.Win32 subnamespace",
            "public static class Fixture { public static System.Type Use() => typeof(Microsoft.Win32.SafeHandles.SafeRegistryHandle); }",
            "registry",
            PluginScanSeverity.Clean);
        yield return Case(
            "System.Reflection.Emit",
            "public static class Fixture { public static System.Type Use() => typeof(System.Reflection.Emit.DynamicMethod); }",
            "reflectionEmit",
            PluginScanSeverity.Block);
        yield return Case(
            "System.Runtime.InteropServices.Marshal",
            "public static class Fixture { public static int Use() => System.Runtime.InteropServices.Marshal.SizeOf<int>(); }",
            "nativeInterop",
            PluginScanSeverity.Clean);
        yield return Case(
            "System.Runtime.InteropServices.NativeLibrary",
            "public static class Fixture { public static System.IntPtr Use() => System.Runtime.InteropServices.NativeLibrary.Load(\"native\"); }",
            "nativeInterop",
            PluginScanSeverity.Clean);
        yield return Case(
            "System.Runtime.InteropServices.GCHandle",
            "public static class Fixture { public static System.Runtime.InteropServices.GCHandle Use(object value) => System.Runtime.InteropServices.GCHandle.Alloc(value); }",
            "nativeInterop",
            PluginScanSeverity.Clean);
        yield return Case("P/Invoke", PInvokeSource, "pInvoke", PluginScanSeverity.Block);
        yield return Case(
            "Assembly.Load",
            "public static class Fixture { public static object Use(byte[] image) => System.Reflection.Assembly.Load(image); }",
            "assemblyLoad",
            PluginScanSeverity.Block);
        yield return Case(
            "Assembly.LoadFrom",
            "public static class Fixture { public static object Use(string path) => System.Reflection.Assembly.LoadFrom(path); }",
            "assemblyLoad",
            PluginScanSeverity.Block);
        yield return Case(
            "Assembly.LoadFile",
            "public static class Fixture { public static object Use(string path) => System.Reflection.Assembly.LoadFile(path); }",
            "assemblyLoad",
            PluginScanSeverity.Block);
        yield return Case(
            "Assembly.UnsafeLoadFrom",
            "public static class Fixture { public static object Use(string path) => System.Reflection.Assembly.UnsafeLoadFrom(path); }",
            "assemblyLoad",
            PluginScanSeverity.Block);
        yield return Case(
            "Assembly.LoadModule",
            "public static class Fixture { public static object Use(byte[] image) => System.Reflection.Assembly.GetExecutingAssembly().LoadModule(\"fixture\", image); }",
            "assemblyLoad",
            PluginScanSeverity.Clean);
        yield return Case(
            "AssemblyLoadContext",
            "public static class Fixture { public static System.Type Use() => typeof(System.Runtime.Loader.AssemblyLoadContext); }",
            "assemblyLoad",
            PluginScanSeverity.Block);
        yield return Case(
            "Environment read",
            "public static class Fixture { public static string Use() => System.Environment.MachineName; }",
            "environment",
            PluginScanSeverity.Clean);
        yield return Case("Environment write", EnvironmentWriteSource, "environment", PluginScanSeverity.Warn);
        yield return Case(
            "AppDomain",
            "public static class Fixture { public static object Use() => System.AppDomain.CurrentDomain; }",
            "appDomain",
            PluginScanSeverity.Clean);

        foreach (var type in new[]
                 {
                     "Thread",
                     "ThreadPool",
                     "Timer",
                     "PeriodicTimer",
                     "Mutex",
                     "Semaphore",
                     "WaitHandle",
                     "EventWaitHandle",
                     "AutoResetEvent",
                     "ManualResetEvent",
                     "RegisteredWaitHandle",
                     "Overlapped",
                     "NativeOverlapped",
                     "PreAllocatedOverlapped",
                 })
        {
            yield return Case(
                $"System.Threading.{type}",
                $"public static class Fixture {{ public static System.Type Use() => typeof(System.Threading.{type}); }}",
                "threading",
                PluginScanSeverity.Clean);
        }

        foreach (var type in new[]
                 {
                     "IBrokerClient",
                     "IMarketDataHub",
                     "IMarketDataIngest",
                     "IMarketDataStore",
                     "InstrumentDataView",
                     "IQuestDbLauncher",
                 })
        {
            yield return Case(
                $"TradingTerminal.Core.MarketData.{type}",
                $"public static class Fixture {{ public static System.Type Use() => typeof(TradingTerminal.Core.MarketData.{type}); }}",
                "hostAccess",
                PluginScanSeverity.Clean);
        }
    }

    public static IEnumerable<object[]> HostBoundaryCases()
    {
        foreach (var type in new[]
                 {
                     "TradingTerminal.Infrastructure.SandboxProbe.Marker",
                     "TradingTerminal.MarketData.SandboxProbe.Marker",
                     "TradingTerminal.Backtest.SandboxProbe.Marker",
                     "TradingTerminal.App.SandboxProbe.Marker",
                     "TradingTerminal.Execution.SandboxProbe.Marker",
                     "TradingTerminal.UI.SandboxProbe.Marker",
                     "TradingTerminal.Login.SandboxProbe.Marker",
                     "TradingTerminal.Settings.SandboxProbe.Marker",
                     "TradingTerminal.Recording.SandboxProbe.Marker",
                     "TradingTerminal.Core.Brokers.SandboxProbe.Marker",
                     "TradingTerminal.Core.Trading.SandboxProbe.Marker",
                     "TradingTerminal.Core.Backtest.SandboxProbe.Marker",
                     "TradingTerminal.Core.Backtesting.SandboxProbe.Marker",
                     "TradingTerminal.Extensions.SecretStore",
                     "TradingTerminal.Extensions.SecretRepository",
                     "TradingTerminal.Extensions.SecretBrokerClient",
                     "TradingTerminal.Extensions.SecretBrokerSelector",
                 })
        {
            yield return [type, "hostAccess"];
        }
    }

    public static IEnumerable<object[]> PermissionCases()
    {
        yield return ["fileIo", FileIoSource];
        yield return ["network", NetworkSource];
        yield return ["environment", EnvironmentWriteSource];
    }

    [Theory]
    [MemberData(nameof(ForbiddenCases))]
    public void Sandbox_blocks_analyzer_forbidden_capability_while_curated_keeps_legacy_outcome(
        string capability,
        string source,
        string sandboxRule,
        PluginScanSeverity curatedVerdict)
    {
        var image = Compile(source);

        var sandbox = PluginPolicyScanner.ScanImage(
            image,
            capability + ".dll",
            profile: PluginScanProfile.Sandbox);
        var curatedDefault = PluginPolicyScanner.ScanImage(image, capability + ".dll");
        var curatedExplicit = PluginPolicyScanner.ScanImage(
            image,
            capability + ".dll",
            profile: PluginScanProfile.Curated);

        Assert.Equal(PluginScanSeverity.Block, sandbox.Verdict);
        Assert.Contains(sandbox.Findings,
            finding => finding.Rule == sandboxRule && finding.Severity == PluginScanSeverity.Block);
        Assert.Equal(curatedVerdict, curatedDefault.Verdict);
        AssertReportsEqual(curatedDefault, curatedExplicit);
        if (curatedVerdict == PluginScanSeverity.Clean)
            Assert.Empty(curatedDefault.Findings);
    }

    [Theory]
    [MemberData(nameof(HostBoundaryCases))]
    public void Sandbox_blocks_analyzer_host_boundary_while_curated_stays_clean(
        string type,
        string sandboxRule)
    {
        var source =
            $"public static class Fixture {{ public static System.Type Use() => typeof({type}); }}";
        var image = Compile(source, additionalReferences: [SyntheticHostReference.Value]);

        var sandbox = PluginPolicyScanner.ScanImage(
            image,
            type + ".dll",
            profile: PluginScanProfile.Sandbox);
        var curated = PluginPolicyScanner.ScanImage(image, type + ".dll");

        Assert.Equal(PluginScanSeverity.Block, sandbox.Verdict);
        Assert.Contains(sandbox.Findings,
            finding => finding.Rule == sandboxRule && finding.Severity == PluginScanSeverity.Block);
        Assert.Equal(PluginScanSeverity.Clean, curated.Verdict);
        Assert.Empty(curated.Findings);
    }

    [Fact]
    public void Sandbox_does_not_block_an_unreferenced_suffix_only_type_definition()
    {
        var image = Compile("""
            namespace TradingTerminal.Extensions
            {
                public sealed class UnusedStore { }
            }
            """);

        var report = PluginPolicyScanner.ScanImage(
            image,
            "UnusedStore.dll",
            profile: PluginScanProfile.Sandbox);

        Assert.Equal(PluginScanSeverity.Clean, report.Verdict);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Sandbox_blocks_a_type_definition_inside_an_analyzer_forbidden_namespace()
    {
        var image = Compile("""
            namespace System.IO.SandboxProbe
            {
                public sealed class Marker { }
            }
            """);

        var report = PluginPolicyScanner.ScanImage(
            image,
            "ForbiddenNamespace.dll",
            profile: PluginScanProfile.Sandbox);

        Assert.Equal(PluginScanSeverity.Block, report.Verdict);
        Assert.Contains(report.Findings,
            finding => finding.Rule == "fileIo" && finding.Severity == PluginScanSeverity.Block);
    }

    [Theory]
    [MemberData(nameof(PermissionCases))]
    public void Sandbox_ignores_manifest_self_grants_while_curated_preserves_them(
        string permission,
        string source)
    {
        var image = Compile(source);

        var curatedWarn = PluginPolicyScanner.ScanImage(image, permission + ".dll");
        var curatedGranted = PluginPolicyScanner.ScanImage(
            image,
            permission + ".dll",
            declaredPermissions: [permission]);
        var sandboxGranted = PluginPolicyScanner.ScanImage(
            image,
            permission + ".dll",
            declaredPermissions: [permission],
            profile: PluginScanProfile.Sandbox);

        Assert.Equal(PluginScanSeverity.Warn, curatedWarn.Verdict);
        Assert.Contains(curatedWarn.Findings,
            finding => finding.Rule == permission && finding.Severity == PluginScanSeverity.Warn);
        Assert.Equal(PluginScanSeverity.Clean, curatedGranted.Verdict);
        Assert.Contains(curatedGranted.Findings,
            finding => finding.Rule == permission && finding.Severity == PluginScanSeverity.Clean);
        Assert.Equal(PluginScanSeverity.Block, sandboxGranted.Verdict);
        Assert.Contains(sandboxGranted.Findings,
            finding => finding.Rule == permission && finding.Severity == PluginScanSeverity.Block);
    }

    [Fact]
    public void Sandbox_allowed_surface_stays_clean()
    {
        var image = Compile("""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using System.Text;
            using System.Threading;
            using System.Threading.Tasks;
            using DaxAlgo.Sdk;

            public static class AllowedSurface
            {
                public static async Task<double> Evaluate(
                    IMarketDataView data,
                    IVirtualBook book,
                    IParameters parameters,
                    IAlertSink alerts)
                {
                    using var gate = new SemaphoreSlim(1, 1);
                    var values = new List<double> { 1d, 4d, 9d };
                    var text = new StringBuilder().Append("sandbox").Append(values.Count).ToString();
                    var first = Task.Run(() => values.Where(value => value > 0d).Sum());
                    var second = Task.Factory.StartNew(() => Math.Sqrt(values.Max()));
                    await first;
                    await second;
                    _ = data;
                    _ = book;
                    _ = parameters;
                    _ = alerts;
                    return first.Result + second.Result + text.Length;
                }
            }
            """);

        var report = PluginPolicyScanner.ScanImage(
            image,
            "AllowedSurface.dll",
            profile: PluginScanProfile.Sandbox);

        Assert.True(
            report.Verdict == PluginScanSeverity.Clean,
            string.Join(Environment.NewLine, report.Findings.Select(finding => finding.Detail)));
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Sandbox_allows_modern_lowering_and_contained_runtime_helpers()
    {
        var image = Compile("""
            using System;
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using System.Threading.Tasks;

            public static class LoweringFixture
            {
                public static async Task<int> WhenAll()
                {
                    var a = Task.FromResult(3);
                    var b = Task.FromResult(5);
                    var results = await Task.WhenAll(a, b);
                    return results[0] + results[1];
                }

                public static int SpanAndHelpers()
                {
                    Span<int> stack = stackalloc int[2];
                    stack[0] = 2;
                    stack[1] = 4;
                    int[] values = [stack[0], stack[1]];
                    ReadOnlySpan<int> readOnly = values;
                    ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(readOnly);
                    ReadOnlySpan<int> roundTrip = MemoryMarshal.Cast<byte, int>(bytes);
                    var list = new List<int>(values);
                    Span<int> listValues = CollectionsMarshal.AsSpan(list);
                    _ = typeof(SafeHandle);
                    var size = Unsafe.SizeOf<int>();
                    var hasReferences = RuntimeHelpers.IsReferenceOrContainsReferences<int>();
                    return roundTrip[0] + listValues[1] + size + (hasReferences ? 1 : 0);
                }
            }
            """);

        var report = PluginPolicyScanner.ScanImage(
            image,
            "ModernLowering.dll",
            profile: PluginScanProfile.Sandbox);

        Assert.True(
            report.Verdict == PluginScanSeverity.Clean,
            string.Join(Environment.NewLine, report.Findings.Select(finding => finding.Detail)));
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void ScanSandboxImage_uses_the_strict_profile()
    {
        var report = PluginPolicyScanner.ScanSandboxImage(Compile(FileIoSource), "FileIo.dll");

        Assert.Equal(PluginScanSeverity.Block, report.Verdict);
        Assert.Contains(report.Findings,
            finding => finding.Rule == "fileIo" && finding.Severity == PluginScanSeverity.Block);
    }

    [Fact]
    public void Sandbox_loader_requires_a_state_store_for_quarantine()
    {
        var error = Assert.Throws<ArgumentNullException>(() => PluginLoader.LoadSandboxedWithReport(
            new ServiceCollection(),
            "missing-sandbox-root",
            SdkInfo.Version,
            PluginTrustPolicy.Permissive,
            state: null!));

        Assert.Equal("state", error.ParamName);
    }

    [Fact]
    public void Sandbox_loader_skips_blocked_artifact_and_reuses_existing_quarantine()
    {
        var root = Path.Combine(Path.GetTempPath(), "daxalgo-tests", "sandbox-loader-" + Guid.NewGuid().ToString("N"));
        var pluginName = "BlockedSandboxPlugin";
        var pluginDirectory = Path.Combine(root, pluginName);
        var sentinel = Path.Combine(root, "plugin-executed.txt");
        Directory.CreateDirectory(pluginDirectory);

        try
        {
            var source = $$"""
                using System.IO;
                using DaxAlgo.Sdk;

                public sealed class BlockedSandboxPlugin : IStrategyPlugin
                {
                    static BlockedSandboxPlugin() => File.WriteAllText({{CSharpLiteral(sentinel)}}, "executed");

                    public string Name => "Blocked sandbox plugin";
                    public string TargetSdkVersion => "{{SdkInfo.Version}}";
                    public void Register(IPluginRegistrar registrar) { }
                }
                """;
            File.WriteAllBytes(Path.Combine(pluginDirectory, pluginName + ".dll"), Compile(source));
            var state = new PluginStateStore(root);

            var report = PluginLoader.LoadSandboxedWithReport(
                new ServiceCollection(),
                root,
                SdkInfo.Version,
                PluginTrustPolicy.Permissive,
                state);

            Assert.Empty(report.Loaded);
            var problem = Assert.Single(report.Problems);
            Assert.Equal(pluginName, problem.PluginFolderName);
            Assert.Equal(PluginLoadOutcome.BlockedByScan, problem.Outcome);
            var quarantine = state.QuarantineFor(pluginName);
            Assert.NotNull(quarantine);
            Assert.Equal(problem.Reason, quarantine.Reason);
            Assert.False(File.Exists(sentinel), "blocked plugin code must not execute");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort test cleanup; the loader should not have opened the blocked assembly.
            }
        }
    }

    private static object[] Case(
        string capability,
        string source,
        string sandboxRule,
        PluginScanSeverity curatedVerdict) =>
        [capability, source, sandboxRule, curatedVerdict];

    private static byte[] Compile(
        string source,
        string? assemblyName = null,
        IReadOnlyList<MetadataReference>? additionalReferences = null)
    {
        var references = RuntimeReferences.Value.AsEnumerable();
        if (additionalReferences is not null)
            references = references.Concat(additionalReferences);

        var compilation = CSharpCompilation.Create(
            assemblyName ?? "PolicyFixture_" + Guid.NewGuid().ToString("N"),
            syntaxTrees: [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            references: references,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: true)
                .WithDeterministic(true));

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(
            emit.Success,
            string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        return stream.ToArray();
    }

    private static MetadataReference[] BuildReferences()
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            paths.TryAdd(Path.GetFileNameWithoutExtension(path), path);

        Add(typeof(IMarketDataView).Assembly.Location);
        Add(typeof(InstrumentId).Assembly.Location);
        Add(typeof(PluginPolicyScanner).Assembly.Location);
        Add(typeof(IServiceCollection).Assembly.Location);
        return paths.Values.Select(path => MetadataReference.CreateFromFile(path)).ToArray();

        void Add(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                paths.TryAdd(Path.GetFileNameWithoutExtension(path), path);
        }
    }

    private static void AssertReportsEqual(PluginScanReport expected, PluginScanReport actual)
    {
        Assert.Equal(expected.Verdict, actual.Verdict);
        Assert.Equal(
            expected.Findings.Select(finding => (finding.Assembly, finding.Rule, finding.Severity, finding.Detail)),
            actual.Findings.Select(finding => (finding.Assembly, finding.Rule, finding.Severity, finding.Detail)));
    }

    private static string CSharpLiteral(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private const string FileIoSource =
        "public static class Fixture { public static string Use() => System.IO.File.ReadAllText(\"data.csv\"); }";

    private const string NetworkSource =
        "public static class Fixture { public static object Use() => new System.Net.Http.HttpClient(); }";

    private const string EnvironmentWriteSource =
        "public static class Fixture { public static void Use() => System.Environment.SetEnvironmentVariable(\"DAXALGO_TEST\", \"1\"); }";

    private const string PInvokeSource = """
        public static class Fixture
        {
            [System.Runtime.InteropServices.DllImport("native")]
            public static extern void Use();
        }
        """;
}
