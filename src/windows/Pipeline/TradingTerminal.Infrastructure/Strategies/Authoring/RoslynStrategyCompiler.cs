using System.IO;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
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
    private const string KernelUsings = """
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
        global using TradingTerminal.Core.Strategies;
        global using TradingTerminal.Core.Strategies.Parameters;
        """;

    /// <summary>Additionally imported when the host actually ships UI.Core — i.e. in the app, where an
    /// authored plugin may carry a live view-model. A headless host (the backtest CLI) has no UI.Core, and
    /// a global using of a namespace that doesn't exist would fail EVERY compile there, so it is
    /// conditional rather than constant.</summary>
    private const string LiveWindowUsings = """
        global using Microsoft.Extensions.Logging;
        global using TradingTerminal.Core.Notifications;
        global using TradingTerminal.UI;
        """;

    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Latest);

    public StrategyCompileResult Compile(StrategyScript script)
    {
        ArgumentNullException.ThrowIfNull(script);

        if (script.Files.Count == 0)
            return StrategyCompileResult.Failed([Error("DAX1002", "There is no source to compile.")]);

        var references = BuildReferences(out var available);
        var globals = available.Contains("TradingTerminal.UI.Core")
            ? $"{KernelUsings}\n{LiveWindowUsings}"
            : KernelUsings;

        // One tree per authored file, keyed by its name — so a diagnostic points at the file the user is
        // looking at (and the model can read its own errors back per file).
        var trees = new List<SyntaxTree>(script.Files.Count + 1)
        {
            CSharpSyntaxTree.ParseText(globals, ParseOptions, path: "GlobalUsings.g.cs"),
        };
        foreach (var file in script.Files)
            trees.Add(CSharpSyntaxTree.ParseText(file.Content, ParseOptions, path: FileName(file, script)));

        var compilation = CSharpCompilation.Create(
            assemblyName: $"DaxAlgo.Authored.{Sanitize(script.Id)}.{Guid.NewGuid():N}",
            syntaxTrees: trees,
            references: references,
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
            // Loaded from the byte[], never the file — so the DLL we persist alongside it is not locked
            // and a regenerate can overwrite it.
            var assembly = Assembly.Load(image);
            var option = BuildOption(script, assembly, out var kernelType, out var bindError);
            if (option is null || kernelType is null)
                return StrategyCompileResult.Failed(
                    Append(diagnostics, Error("DAX1000", bindError ?? "Could not bind the strategy type.")));

            var authored = new AuthoredStrategyAssembly(
                image, assembly, kernelType,
                DescriptorType: FindDescriptor(assembly),
                ViewModelType: FindLiveViewModel(assembly),
                ViewType: FindView(assembly));

            return StrategyCompileResult.Succeeded(option, diagnostics, authored);
        }
        catch (Exception ex)
        {
            return StrategyCompileResult.Failed(
                Append(diagnostics, Error("DAX1001", $"Strategy load failed: {ex.Message}")));
        }
    }

    /// <summary>
    /// The catalog descriptor, if the author wrote one. Matched by interface, not by name — Core's
    /// <c>ITradingStrategy</c> is the same type in the authored assembly (it resolves from the default
    /// load context).
    /// </summary>
    private static Type? FindDescriptor(Assembly assembly) => assembly.GetTypes().FirstOrDefault(t =>
        t is { IsClass: true, IsAbstract: false } &&
        typeof(ITradingStrategy).IsAssignableFrom(t) &&
        t.GetConstructor(Type.EmptyTypes) is not null);

    /// <summary>The live view-model, if written. Matched by base-type NAME: the base lives in
    /// <c>TradingTerminal.UI.Core</c>, which Infrastructure does not (and must not) reference — but the
    /// authored assembly compiles against it, because it is in the host's trusted-platform set.</summary>
    private static Type? FindLiveViewModel(Assembly assembly) =>
        assembly.GetTypes().FirstOrDefault(t => t is { IsClass: true, IsAbstract: false } &&
            InheritsFrom(t, "TradingTerminal.UI.LiveSignalStrategyViewModelBase"));

    /// <summary>The live view, if written — a code-built WPF control (Roslyn cannot compile XAML, so an
    /// authored view builds its tree in C#). Matched by base-type name for the same layering reason.</summary>
    private static Type? FindView(Assembly assembly) =>
        assembly.GetTypes().FirstOrDefault(t => t is { IsClass: true, IsAbstract: false } &&
            (InheritsFrom(t, "System.Windows.Controls.UserControl") ||
             InheritsFrom(t, "System.Windows.Window")));

    private static bool InheritsFrom(Type type, string baseFullName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, baseFullName, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Resolves the single <see cref="IBacktestStrategy"/> class and wires its factory
    /// (and optional declarative-parameter members) into a <see cref="BacktestStrategyOption"/>.</summary>
    private static BacktestStrategyOption? BuildOption(
        StrategyScript script, Assembly assembly, out Type? kernelType, out string? error)
    {
        error = null;
        kernelType = null;
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

        kernelType = type;
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

    /// <summary>
    /// The trusted-platform set — which for a .NET app is the framework AND every assembly the host
    /// ships (its deps.json): Core (the strategy contract), UI / UI.Core (so an authored plugin can build
    /// a live view-model + view), the SDK, MVVM, DI abstractions, WPF. Identity is preserved because the
    /// authored assembly resolves them from the default load context.
    /// <para>
    /// Deliberately NOT the loaded-assembly list: strategy plugins live in their own
    /// <c>AssemblyLoadContext</c>, so compiling against one and then loading into the default context
    /// would bind two different <c>Type</c> identities for the same name. Authored code sees the host's
    /// surface, not other plugins'.
    /// </para>
    /// </summary>
    /// <param name="available">Simple names of every assembly the authored code may reference — used to
    /// decide whether the live-window global usings can be injected at all.</param>
    private static IReadOnlyList<MetadataReference> BuildReferences(out HashSet<string> available)
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        available = seen;

        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // Dedupe by simple name — the same assembly from two paths is a CS1704.
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                seen.Add(Path.GetFileNameWithoutExtension(path)))
                references.Add(MetadataReference.CreateFromFile(path));
        }

        // Core (IBacktestStrategy, Contract, StrategyParameters, …) — belt and braces if it somehow
        // wasn't in the platform set.
        var core = typeof(IBacktestStrategy).Assembly;
        if (!string.IsNullOrEmpty(core.Location) && seen.Add(Path.GetFileNameWithoutExtension(core.Location)))
            references.Add(MetadataReference.CreateFromFile(core.Location));

        return references;
    }

    /// <summary>The compilation path for a file — its authored name, sanitized, so diagnostics carry a
    /// name the user recognizes from the editor's file list.</summary>
    private static string FileName(StrategyFile file, StrategyScript script) =>
        string.IsNullOrWhiteSpace(file.Name) ? $"{Sanitize(script.Id)}.cs" : file.Name;

    private static StrategyDiagnostic Map(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        var position = span.StartLinePosition;
        var severity = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => StrategyDiagnosticSeverity.Error,
            DiagnosticSeverity.Warning => StrategyDiagnosticSeverity.Warning,
            _ => StrategyDiagnosticSeverity.Info,
        };
        return new StrategyDiagnostic(
            severity, diagnostic.Id, diagnostic.GetMessage(),
            position.Line + 1, position.Character + 1,
            File: span.Path ?? string.Empty);
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
