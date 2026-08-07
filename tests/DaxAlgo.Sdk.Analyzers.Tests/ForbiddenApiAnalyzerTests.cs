using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Domain;
using Xunit;

namespace DaxAlgo.Sdk.Analyzers.Tests;

public sealed class ForbiddenApiAnalyzerTests
{
    private const string KernelMarker = """
        using DaxAlgo.Sdk;

        internal sealed class TestKernel : IStrategyKernel
        {
            public TradingTerminal.Core.Strategies.Parameters.StrategyParameterSchema Schema
                => TradingTerminal.Core.Strategies.Parameters.StrategyParameterSchema.Empty;

            public TradingTerminal.Core.Strategies.StrategyDataRequirement DataRequirement
                => TradingTerminal.Core.Strategies.StrategyDataRequirement.None;

            public System.Threading.Tasks.Task OnStartAsync(
                IStrategyRuntimeContext context,
                System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;
        }

        """;

    private static readonly ReferenceAssemblies Net90 = new(
        "net9.0",
        new PackageIdentity("Microsoft.NETCore.App.Ref", "9.0.0"),
        Path.Combine("ref", "net9.0"));

    public static IEnumerable<object[]> ForbiddenCases()
    {
        yield return Case(
            "System.IO",
            "read market data via IMarketDataView.",
            "internal static class Helper { internal static string Read() => System.IO.File.{|#0:ReadAllText|}(\"data.csv\"); }");
        yield return Case(
            "System.Net",
            "network access is host-mediated; read market data via IMarketDataView.",
            "internal static class Helper { internal static object Open() => new System.Net.Http.{|#0:HttpClient|}(); }");
        yield return Case(
            "System.Diagnostics.Process",
            OsGuidance,
            "internal static class Helper { internal static void Start() => System.Diagnostics.Process.{|#0:Start|}(\"tool\"); }");
        yield return Case(
            "System.Diagnostics.ProcessStartInfo",
            OsGuidance,
            "internal static class Helper { internal static object StartInfo() => new System.Diagnostics.{|#0:ProcessStartInfo|}(\"tool\"); }");
        yield return Case(
            "System.Reflection.Emit",
            "runtime code generation is not available in the sandbox.",
            "internal static class Helper { internal static object Emit() => new System.Reflection.Emit.{|#0:DynamicMethod|}(\"M\", typeof(void), System.Type.EmptyTypes); }");
        yield return Case(
            "System.Runtime.InteropServices",
            "native interop and P/Invoke are not available in the sandbox.",
            "internal static class Helper { [System.Runtime.InteropServices.{|#0:DllImport|}(\"native\")] internal static extern void Call(); }");
        yield return Case(
            "System.Environment",
            OsGuidance,
            "internal static class Helper { internal static string Machine => System.Environment.{|#0:MachineName|}; }");
        yield return Case(
            "Microsoft.Win32",
            "registry and host-configuration access are not available in the sandbox.",
            "internal static class Helper { internal static object Registry => Microsoft.Win32.Registry.{|#0:CurrentUser|}; }");
        yield return Case(
            "System.Reflection.Assembly.Load*",
            "runtime assembly loading is not available in the sandbox.",
            "internal static class Helper { internal static object Load(byte[] bytes) => System.Reflection.Assembly.{|#0:Load|}(bytes); }");
        yield return Case(
            "System.Runtime.Loader.AssemblyLoadContext",
            "runtime assembly loading is not available in the sandbox.",
            "internal static class Helper { internal static object Context => System.Runtime.Loader.AssemblyLoadContext.{|#0:Default|}; }");
        yield return Case(
            "System.AppDomain",
            OsGuidance,
            "internal static class Helper { internal static object Domain => System.AppDomain.{|#0:CurrentDomain|}; }");

        foreach (var threadingCase in ThreadingCases())
            yield return threadingCase;

        yield return Case(
            "TradingTerminal.Core.Trading",
            HostGuidance,
            "internal sealed class Helper { internal TradingTerminal.Core.Trading.{|#0:IOrderRouter|}? Router { get; init; } }");
        yield return Case(
            "TradingTerminal.Core.Backtesting",
            HostGuidance,
            "internal sealed class Helper { internal TradingTerminal.Core.Backtesting.{|#0:IStrategyContext|}? Context { get; init; } }");
        yield return Case(
            "TradingTerminal.Core.MarketData.IMarketDataStore",
            "read scoped data via IMarketDataView instead of broker, hub, ingest, or store services.",
            "internal sealed class Helper { internal TradingTerminal.Core.MarketData.{|#0:IMarketDataStore|}? Store { get; init; } }");
        yield return Case(
            "TradingTerminal.Core.MarketData.IBrokerClient",
            "read scoped data via IMarketDataView instead of broker, hub, ingest, or store services.",
            "internal sealed class Helper { internal TradingTerminal.Core.MarketData.{|#0:IBrokerClient|}? Broker { get; init; } }");
    }

    [Theory]
    [MemberData(nameof(ForbiddenCases))]
    public async Task SandboxCompilation_ReportsForbiddenReferenceAsError(
        string capability,
        string guidance,
        string source)
    {
        var expected = new DiagnosticResult(ForbiddenApiAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0)
            .WithArguments(capability, guidance);

        await CreateTest(KernelMarker + source, expected).RunAsync();
    }

    [Fact]
    public async Task Visualizer_ActivatesCompilationWideEnforcement()
    {
        const string source = """
            using DaxAlgo.Sdk;

            internal sealed class TestVisualizer : IVisualizer
            {
                public TradingTerminal.Core.Strategies.Parameters.StrategyParameterSchema Schema
                    => TradingTerminal.Core.Strategies.Parameters.StrategyParameterSchema.Empty;

                public TradingTerminal.Core.Strategies.StrategyDataRequirement DataRequirement
                    => TradingTerminal.Core.Strategies.StrategyDataRequirement.None;

                public System.Threading.Tasks.Task OnStartAsync(
                    IVisualizerContext context,
                    System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;
            }

            internal static class Helper
            {
                internal static string Machine => System.Environment.{|#0:MachineName|};
            }
            """;

        var expected = new DiagnosticResult(ForbiddenApiAnalyzer.DiagnosticId, DiagnosticSeverity.Error)
            .WithLocation(0)
            .WithArguments("System.Environment", OsGuidance);

        await CreateTest(source, expected).RunAsync();
    }

    [Fact]
    public async Task RealSandboxSurface_MathAndLinq_HasNoDiagnostic()
    {
        const string source = """
            using System;
            using System.Linq;
            using DaxAlgo.Sdk;
            using TradingTerminal.Core.Domain;
            using TradingTerminal.Core.Strategies;
            using TradingTerminal.Core.Strategies.Parameters;

            internal sealed class AllowedKernel : IStrategyKernel
            {
                public StrategyParameterSchema Schema => StrategyParameterSchema.Empty;

                public StrategyDataRequirement DataRequirement
                    => StrategyDataRequirement.Bars;

                public System.Threading.Tasks.Task OnStartAsync(
                    IStrategyRuntimeContext context,
                    System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;

                internal static double Evaluate(IStrategyRuntimeContext context, InstrumentId instrument)
                {
                    var mean = context.Data
                        .RecentBars(instrument, BarSize.OneMinute, 32)
                        .Select(static bar => bar.Close)
                        .DefaultIfEmpty(0d)
                        .Average();
                    var threshold = context.Parameters.GetDouble("threshold");
                    context.Book.SetTargetPosition(instrument, mean > threshold ? 1d : 0d);
                    context.Alerts.AlertIf(mean > threshold, "Threshold crossed", AlertLevel.Information, "threshold");
                    return Math.Sqrt(Math.Abs(mean));
                }
            }
            """;

        await CreateTest(source).RunAsync();
    }

    [Fact]
    public async Task LegacyOnlyCompilation_DoesNotActivateSandboxEnforcement()
    {
        const string source = """
            internal static class LegacyPluginHelper
            {
                internal static string Read() => System.IO.File.ReadAllText("legacy.txt");
            }
            """;

        await CreateTest(source).RunAsync();
    }

    private static IEnumerable<object[]> ThreadingCases()
    {
        yield return Threading("Thread", "internal static object Create() => new System.Threading.{|#0:Thread|}(() => { });");
        yield return Threading("ThreadPool", "internal static object Type() => typeof(System.Threading.{|#0:ThreadPool|});");
        yield return Threading("Timer", "internal static object Timer() => new System.Threading.{|#0:Timer|}(_ => { }, null, 0, 1);");
        yield return Threading("PeriodicTimer", "internal static object Timer() => new System.Threading.{|#0:PeriodicTimer|}(System.TimeSpan.FromSeconds(1));");
        yield return Threading("Mutex", "internal static object Mutex() => new System.Threading.{|#0:Mutex|}();");
        yield return Threading("Semaphore", "internal static object Semaphore() => new System.Threading.{|#0:Semaphore|}(0, 1);");
        yield return Threading("WaitHandle", "internal static object Use(System.Threading.{|#0:WaitHandle|} handle) => handle;");
        yield return Threading("EventWaitHandle", "internal static object Event() => new System.Threading.{|#0:EventWaitHandle|}(false, System.Threading.EventResetMode.AutoReset);");
        yield return Threading("RegisteredWaitHandle", "internal static object Use(System.Threading.{|#0:RegisteredWaitHandle|} handle) => handle;");
        yield return Threading("Overlapped", "internal static object Native() => new System.Threading.{|#0:Overlapped|}();");
    }

    private static object[] Threading(string type, string member) => Case(
        "escaping System.Threading primitives",
        OsGuidance,
        $"internal static class Helper {{ {member} }}");

    private static object[] Case(string capability, string guidance, string source) =>
        [capability, guidance, source];

    private static CSharpAnalyzerTest<ForbiddenApiAnalyzer, DefaultVerifier> CreateTest(
        string source,
        params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<ForbiddenApiAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = Net90,
        };

        test.TestState.AdditionalReferences.Add(MetadataReference.CreateFromFile(typeof(IStrategyKernel).Assembly.Location));
        test.TestState.AdditionalReferences.Add(MetadataReference.CreateFromFile(typeof(InstrumentId).Assembly.Location));
        test.TestState.AdditionalReferences.Add(MetadataReference.CreateFromFile(typeof(IServiceCollection).Assembly.Location));
        test.ExpectedDiagnostics.AddRange(expected);
        return test;
    }

    private const string OsGuidance =
        "process, operating-system, and wall-clock scheduling access are not available; use IClock and host callbacks.";

    private const string HostGuidance =
        "use IMarketDataView for data, IVirtualBook for intents, and IAlertSink for bounded alerts.";
}
