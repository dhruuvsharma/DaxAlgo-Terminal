using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DaxAlgo.Sdk.Analyzers;

/// <summary>
/// Rejects privileged APIs from projects that declare a sandbox strategy kernel or visualizer.
/// Activation is compilation-wide so forbidden calls cannot be hidden in helper types.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ForbiddenApiAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "DAX3001";

    private const string StrategyKernelMetadataName = "DaxAlgo.Sdk.IStrategyKernel";
    private const string VisualizerMetadataName = "DaxAlgo.Sdk.IVisualizer";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Forbidden API in sandbox strategy",
        "Strategies run in a data-only sandbox; '{0}' is not permitted — {1}",
        "DaxAlgo.Sandbox",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Sandbox strategies and visualizers may use only host-provided data, clock, parameter, virtual-book, and alert capabilities.",
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    private static readonly ImmutableHashSet<string> ForbiddenTypes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "System.Diagnostics.Process",
        "System.Diagnostics.ProcessStartInfo",
        "System.Environment",
        "System.AppDomain",
        "System.Runtime.Loader.AssemblyLoadContext",
        "System.Threading.Thread",
        "System.Threading.ThreadPool",
        "System.Threading.Timer",
        "System.Threading.PeriodicTimer",
        "System.Threading.Mutex",
        "System.Threading.Semaphore",
        "System.Threading.WaitHandle",
        "System.Threading.EventWaitHandle",
        "System.Threading.AutoResetEvent",
        "System.Threading.ManualResetEvent",
        "System.Threading.RegisteredWaitHandle",
        "System.Threading.Overlapped",
        "System.Threading.NativeOverlapped",
        "System.Threading.PreAllocatedOverlapped");

    private static readonly ImmutableHashSet<string> ForbiddenCoreTypes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "TradingTerminal.Core.MarketData.IBrokerClient",
        "TradingTerminal.Core.MarketData.IMarketDataHub",
        "TradingTerminal.Core.MarketData.IMarketDataIngest",
        "TradingTerminal.Core.MarketData.IMarketDataStore",
        "TradingTerminal.Core.MarketData.InstrumentDataView",
        "TradingTerminal.Core.MarketData.IQuestDbLauncher");

    private static readonly string[] ForbiddenHostNamespacePrefixes =
    [
        "TradingTerminal.Infrastructure",
        "TradingTerminal.MarketData",
        // Forward guard. The backtest engine was archived on 2026-08-17 and nothing ships under
        // this prefix today; the entry stays so the engine issue #36 designs cannot reach
        // sandboxed code by default when it lands.
        "TradingTerminal.Backtest",
        "TradingTerminal.App",
        "TradingTerminal.Execution",
        "TradingTerminal.UI",
        "TradingTerminal.Login",
        "TradingTerminal.Settings",
        "TradingTerminal.Recording",
        "TradingTerminal.Core.Brokers",
        "TradingTerminal.Core.Trading",
        "TradingTerminal.Core.Backtest",
    ];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(startContext =>
        {
            var kernel = startContext.Compilation.GetTypeByMetadataName(StrategyKernelMetadataName);
            var visualizer = startContext.Compilation.GetTypeByMetadataName(VisualizerMetadataName);
            if (kernel is null && visualizer is null)
                return;

            var state = new CompilationState(kernel, visualizer);
            startContext.RegisterSymbolAction(state.AnalyzeNamedType, SymbolKind.NamedType);
            startContext.RegisterSyntaxNodeAction(
                state.AnalyzeName,
                SyntaxKind.IdentifierName,
                SyntaxKind.GenericName);
            startContext.RegisterCompilationEndAction(state.ReportDiagnostics);
        });
    }

    private sealed class CompilationState
    {
        private readonly INamedTypeSymbol? _kernel;
        private readonly INamedTypeSymbol? _visualizer;
        private readonly ConcurrentQueue<PendingDiagnostic> _pending = new();
        private int _active;

        public CompilationState(INamedTypeSymbol? kernel, INamedTypeSymbol? visualizer)
        {
            _kernel = kernel;
            _visualizer = visualizer;
        }

        public void AnalyzeNamedType(SymbolAnalysisContext context)
        {
            var type = (INamedTypeSymbol)context.Symbol;
            if (!type.Locations.Any(static location => location.IsInSource))
                return;

            if (type.AllInterfaces.Any(candidate =>
                    SymbolEqualityComparer.Default.Equals(candidate, _kernel)
                    || SymbolEqualityComparer.Default.Equals(candidate, _visualizer)))
                Interlocked.Exchange(ref _active, 1);
        }

        public void AnalyzeName(SyntaxNodeAnalysisContext context)
        {
            var name = (SimpleNameSyntax)context.Node;
            if (name.Ancestors().Any(static node => node is UsingDirectiveSyntax))
                return;

            var top = GetAccessChain(name);
            if (!ReferenceEquals(name, GetRightmostName(top)))
                return;

            var symbolInfo = context.SemanticModel.GetSymbolInfo(top, context.CancellationToken);
            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
            if (symbol is IAliasSymbol alias)
                symbol = alias.Target;
            if (symbol is null || !TryMatch(symbol, out var match))
                return;

            _pending.Enqueue(new PendingDiagnostic(
                name.Identifier.GetLocation(),
                match.Capability,
                match.Guidance));
        }

        public void ReportDiagnostics(CompilationAnalysisContext context)
        {
            if (Volatile.Read(ref _active) == 0)
                return;

            while (_pending.TryDequeue(out var pending))
                context.ReportDiagnostic(Diagnostic.Create(
                    Rule,
                    pending.Location,
                    pending.Capability,
                    pending.Guidance));
        }
    }

    private static SyntaxNode GetAccessChain(SimpleNameSyntax name)
    {
        SyntaxNode current = name;
        while (current.Parent is QualifiedNameSyntax qualified
               && (ReferenceEquals(qualified.Left, current) || ReferenceEquals(qualified.Right, current)))
            current = qualified;
        while (current.Parent is AliasQualifiedNameSyntax aliasQualified
               && (ReferenceEquals(aliasQualified.Alias, current) || ReferenceEquals(aliasQualified.Name, current)))
            current = aliasQualified;
        while (current.Parent is MemberAccessExpressionSyntax memberAccess
               && (ReferenceEquals(memberAccess.Expression, current) || ReferenceEquals(memberAccess.Name, current)))
            current = memberAccess;
        return current;
    }

    private static SimpleNameSyntax? GetRightmostName(SyntaxNode node) => node switch
    {
        SimpleNameSyntax simple => simple,
        QualifiedNameSyntax qualified => GetRightmostName(qualified.Right),
        AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name,
        MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
        _ => null,
    };

    private static bool TryMatch(ISymbol symbol, out ForbiddenMatch match)
    {
        var containingType = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        var typeName = containingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var namespaceName = symbol is INamespaceSymbol namespaceSymbol
            ? namespaceSymbol.ToDisplayString()
            : containingType?.ContainingNamespace.ToDisplayString()
                ?? symbol.ContainingNamespace?.ToDisplayString();

        if (IsNamespace(namespaceName, "System.IO"))
            return Found("System.IO", "read market data via IMarketDataView.", out match);
        if (IsNamespace(namespaceName, "System.Net"))
            return Found("System.Net", "network access is host-mediated; read market data via IMarketDataView.", out match);
        if (IsNamespace(namespaceName, "System.Reflection.Emit"))
            return Found("System.Reflection.Emit", "runtime code generation is not available in the sandbox.", out match);
        if (IsNamespace(namespaceName, "System.Runtime.InteropServices"))
            return Found("System.Runtime.InteropServices", "native interop and P/Invoke are not available in the sandbox.", out match);
        if (IsNamespace(namespaceName, "Microsoft.Win32"))
            return Found("Microsoft.Win32", "registry and host-configuration access are not available in the sandbox.", out match);

        if (typeName == "System.Reflection.Assembly"
            && symbol is IMethodSymbol assemblyMethod
            && assemblyMethod.Name.StartsWith("Load", StringComparison.Ordinal))
            return Found("System.Reflection.Assembly.Load*", "runtime assembly loading is not available in the sandbox.", out match);

        if (typeName is not null && ForbiddenTypes.Contains(typeName))
        {
            var capability = typeName.StartsWith("System.Threading.", StringComparison.Ordinal)
                ? "escaping System.Threading primitives"
                : typeName;
            var guidance = typeName is "System.Runtime.Loader.AssemblyLoadContext"
                ? "runtime assembly loading is not available in the sandbox."
                : "process, operating-system, and wall-clock scheduling access are not available; use IClock and host callbacks.";
            return Found(capability, guidance, out match);
        }

        if (typeName is not null && ForbiddenCoreTypes.Contains(typeName))
            return Found(typeName, "read scoped data via IMarketDataView instead of broker, hub, ingest, or store services.", out match);

        if (namespaceName is not null
            && ForbiddenHostNamespacePrefixes.Any(prefix => IsNamespace(namespaceName, prefix)))
            return Found(namespaceName, "use IMarketDataView for data, IVirtualBook for intents, and IAlertSink for bounded alerts.", out match);

        if (typeName is not null
            && IsNamespace(namespaceName, "TradingTerminal")
            && (containingType!.Name.EndsWith("Store", StringComparison.Ordinal)
                || containingType.Name.EndsWith("Repository", StringComparison.Ordinal)
                || containingType.Name.EndsWith("BrokerClient", StringComparison.Ordinal)
                || containingType.Name.EndsWith("BrokerSelector", StringComparison.Ordinal)))
            return Found(typeName, "host stores, brokers, and execution services are not sandbox capabilities.", out match);

        match = default;
        return false;
    }

    private static bool IsNamespace(string? candidate, string prefix) =>
        candidate is not null
        && (string.Equals(candidate, prefix, StringComparison.Ordinal)
            || candidate.StartsWith(prefix + ".", StringComparison.Ordinal));

    private static bool Found(string capability, string guidance, out ForbiddenMatch match)
    {
        match = new ForbiddenMatch(capability, guidance);
        return true;
    }

    private readonly struct ForbiddenMatch
    {
        public ForbiddenMatch(string capability, string guidance)
        {
            Capability = capability;
            Guidance = guidance;
        }

        public string Capability { get; }
        public string Guidance { get; }
    }

    private readonly struct PendingDiagnostic
    {
        public PendingDiagnostic(Location location, string capability, string guidance)
        {
            Location = location;
            Capability = capability;
            Guidance = guidance;
        }

        public Location Location { get; }
        public string Capability { get; }
        public string Guidance { get; }
    }
}
