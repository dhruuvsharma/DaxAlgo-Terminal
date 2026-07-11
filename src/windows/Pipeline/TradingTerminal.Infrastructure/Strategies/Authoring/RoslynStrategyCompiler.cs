using System.IO;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Infrastructure.Plugins;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Roslyn-backed <see cref="IStrategyCompiler"/>. Compiles a user's C# source into an
/// in-memory assembly, reflects out the single class that implements
/// <see cref="IBacktestStrategy"/>, and packages it as a runnable
/// <see cref="BacktestStrategyOption"/> — so an authored strategy is a first-class citizen
/// of the catalog/backtester with no recompile of the host.
///
/// A set of <c>global using</c>s is injected as a separate syntax tree so user source stays
/// terse <em>and</em> compiler diagnostics keep the user's own 1-based line numbers. See the
/// trust-boundary note on <see cref="IStrategyCompiler"/>.
/// </summary>
public sealed class RoslynStrategyCompiler : IStrategyCompiler
{
    /// <summary>Ambient namespaces every script gets for free (kept in a dedicated tree so
    /// they don't shift the user's line numbers).</summary>
    private const string GlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using TradingTerminal.Core.Domain;
        global using TradingTerminal.Core.Trading;
        global using TradingTerminal.Core.Time;
        global using TradingTerminal.Core.Backtest;
        global using TradingTerminal.Core.MarketData;
        global using TradingTerminal.Core.Strategies.Parameters;
        """;

    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Latest);

    public StrategyCompileResult Compile(StrategyScript script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var userTree = CSharpSyntaxTree.ParseText(
            script.SourceCode, ParseOptions, path: $"{Sanitize(script.Id)}.cs");
        var globalsTree = CSharpSyntaxTree.ParseText(GlobalUsings, ParseOptions, path: "GlobalUsings.g.cs");

        var compilation = CSharpCompilation.Create(
            assemblyName: $"DaxAlgo.Authored.{Sanitize(script.Id)}.{Guid.NewGuid():N}",
            syntaxTrees: new[] { globalsTree, userTree },
            references: BuildReferences(),
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Enable,
                allowUnsafe: false));

        using var peStream = new MemoryStream();
        var emit = compilation.Emit(peStream);

        var diagnostics = emit.Diagnostics
            .Where(d => d.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
            .Select(Map)
            .ToArray();

        if (!emit.Success)
            return StrategyCompileResult.Failed(diagnostics);

        // Authored source is untrusted the moment an AI (or a pasted snippet) can write it, and it is
        // about to run in-process with full host privileges. Scan the emitted image with the SAME
        // policy the plugin loader applies — before Assembly.Load, so Block-level code (P/Invoke,
        // starting a process, the registry, Reflection.Emit, loading assemblies) never executes. The
        // scan reads the bytes as data.
        var image = peStream.ToArray();
        var scan = PluginPolicyScanner.ScanImage(image, $"{Sanitize(script.Id)}.dll");
        diagnostics = MergeScan(diagnostics, scan);
        if (scan.Verdict == PluginScanSeverity.Block)
            return StrategyCompileResult.Failed(diagnostics);

        try
        {
            peStream.Position = 0;
            var assembly = Assembly.Load(image);
            var option = BuildOption(script, assembly, out var bindError);
            if (option is null)
                return StrategyCompileResult.Failed(
                    Append(diagnostics, Error("DAX1000", bindError ?? "Could not bind the strategy type.")));

            return StrategyCompileResult.Succeeded(option, diagnostics);
        }
        catch (Exception ex)
        {
            return StrategyCompileResult.Failed(
                Append(diagnostics, Error("DAX1001", $"Strategy load failed: {ex.Message}")));
        }
    }

    /// <summary>Resolves the single <see cref="IBacktestStrategy"/> class and wires its factory
    /// (and optional declarative-parameter members) into a <see cref="BacktestStrategyOption"/>.</summary>
    private static BacktestStrategyOption? BuildOption(StrategyScript script, Assembly assembly, out string? error)
    {
        error = null;
        var candidates = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IBacktestStrategy).IsAssignableFrom(t))
            .ToArray();

        if (candidates.Length == 0)
        {
            error = "No public class implementing IBacktestStrategy was found.";
            return null;
        }
        if (candidates.Length > 1)
        {
            error = $"Found {candidates.Length} IBacktestStrategy classes; define exactly one " +
                    $"({string.Join(", ", candidates.Select(t => t.Name))}).";
            return null;
        }

        var type = candidates[0];
        var ctor = type.GetConstructor(new[] { typeof(Contract) });
        if (ctor is null)
        {
            error = $"'{type.Name}' must declare a public constructor taking a single Contract.";
            return null;
        }

        Func<Contract, IBacktestStrategy> build = contract =>
            (IBacktestStrategy)ctor.Invoke(new object[] { contract });

        var schema = ReadStaticSchema(type);
        var parameterizedBuild = ReadParameterizedBuild(type);

        return new BacktestStrategyOption(script.Id, script.DisplayName, build)
        {
            Schema = schema ?? StrategyParameterSchema.Empty,
            ParameterizedBuild = parameterizedBuild,
        };
    }

    /// <summary>Reads an optional <c>public static StrategyParameterSchema Schema { get; }</c>.</summary>
    private static StrategyParameterSchema? ReadStaticSchema(Type type)
    {
        var prop = type.GetProperty("Schema", BindingFlags.Public | BindingFlags.Static);
        return prop is not null && prop.PropertyType == typeof(StrategyParameterSchema)
            ? prop.GetValue(null) as StrategyParameterSchema
            : null;
    }

    /// <summary>Reads an optional <c>public static IBacktestStrategy Create(Contract, StrategyParameters)</c>.</summary>
    private static Func<Contract, StrategyParameters, IBacktestStrategy>? ReadParameterizedBuild(Type type)
    {
        var method = type.GetMethod(
            "Create", BindingFlags.Public | BindingFlags.Static, binder: null,
            types: new[] { typeof(Contract), typeof(StrategyParameters) }, modifiers: null);

        if (method is null || !typeof(IBacktestStrategy).IsAssignableFrom(method.ReturnType))
            return null;

        return (contract, parameters) =>
            (IBacktestStrategy)method.Invoke(null, new object[] { contract, parameters })!;
    }

    /// <summary>Framework assemblies (from the trusted-platform set) plus the Core assembly that
    /// defines the strategy contract. Core has zero third-party deps, so this is sufficient.</summary>
    private static IReadOnlyList<MetadataReference> BuildReferences()
    {
        var references = new List<MetadataReference>();
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                references.Add(MetadataReference.CreateFromFile(path));
        }

        // Core (IBacktestStrategy, Contract, StrategyParameters, …). Identity is preserved
        // because the loaded assembly resolves Core from the default load context.
        references.Add(MetadataReference.CreateFromFile(typeof(IBacktestStrategy).Assembly.Location));
        return references;
    }

    private static StrategyDiagnostic Map(Diagnostic diagnostic)
    {
        var position = diagnostic.Location.GetLineSpan().StartLinePosition;
        var severity = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => StrategyDiagnosticSeverity.Error,
            DiagnosticSeverity.Warning => StrategyDiagnosticSeverity.Warning,
            _ => StrategyDiagnosticSeverity.Info,
        };
        return new StrategyDiagnostic(
            severity, diagnostic.Id, diagnostic.GetMessage(),
            position.Line + 1, position.Character + 1);
    }

    private static StrategyDiagnostic Error(string id, string message) =>
        new(StrategyDiagnosticSeverity.Error, id, message, 1, 1);

    /// <summary>Turns the policy scan into diagnostics the authoring pane already knows how to show:
    /// a Block-level finding is an Error (and fails the compile), a Warn-level one is a Warning (the
    /// strategy compiles, but the user is told it reaches for file / network I/O). Clean scans add
    /// nothing.</summary>
    private static StrategyDiagnostic[] MergeScan(StrategyDiagnostic[] existing, PluginScanReport scan)
    {
        if (scan.Findings.Count == 0) return existing;

        var merged = existing.ToList();
        foreach (var finding in scan.Findings)
        {
            var severity = finding.Severity switch
            {
                PluginScanSeverity.Block => StrategyDiagnosticSeverity.Error,
                PluginScanSeverity.Warn => StrategyDiagnosticSeverity.Warning,
                _ => StrategyDiagnosticSeverity.Info,
            };
            var message = finding.Severity == PluginScanSeverity.Block
                ? $"Authored strategies may not use this: {finding.Detail}. Blocked by the plugin policy scan."
                : $"This strategy {finding.Detail}.";
            merged.Add(new StrategyDiagnostic(severity, $"DAX2{(int)finding.Severity:D3}", message, 1, 1));
        }
        return [.. merged];
    }

    private static IReadOnlyList<StrategyDiagnostic> Append(
        IReadOnlyList<StrategyDiagnostic> existing, StrategyDiagnostic extra) =>
        existing.Append(extra).ToArray();

    private static string Sanitize(string id) =>
        new(id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
