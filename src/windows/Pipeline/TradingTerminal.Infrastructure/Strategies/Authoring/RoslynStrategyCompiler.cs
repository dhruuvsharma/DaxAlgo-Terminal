using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Infrastructure.Plugins;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Roslyn-backed <see cref="IStrategyCompiler"/>. Compiles a user's C# source into an
/// in-memory assembly, reflects out the single class that implements
/// <see cref="IStrategyKernel"/> or <see cref="IVisualizer"/> — and, for already-installed plugins,
/// the retired <see cref="IOrderRoutedStrategy"/> — packaging the latter as a runnable
/// <see cref="StrategyCatalogEntry"/> so an authored strategy is a first-class citizen
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
        global using TradingTerminal.Core.Strategies;
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
        // looking at (and the model can read its own errors back per file). Plus two generated trees the
        // user never sees: the ambient usings, and the plugin entry point.
        var trees = new List<SyntaxTree>(script.Files.Count + 2)
        {
            CSharpSyntaxTree.ParseText(globals, ParseOptions, path: "GlobalUsings.g.cs"),
            CSharpSyntaxTree.ParseText(PluginEntryPoint(script), ParseOptions, path: "Plugin.g.cs"),
        };
        foreach (var file in script.Files)
            trees.Add(CSharpSyntaxTree.ParseText(file.Content, ParseOptions, path: FileName(file, script)));

        var compilation = CSharpCompilation.Create(
            assemblyName: BuildAssemblyName(script, globals),
            syntaxTrees: trees,
            references: references,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Enable,
                allowUnsafe: false)
                .WithDeterministic(true));

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

            // What did the author actually write? Asked before anything is bound, so a submission written
            // against the current contracts gets a real answer rather than "no IOrderRoutedStrategy found",
            // which is what it used to get and which sent people looking for a mistake they had not made.
            var unit = FindUnit(assembly, out var findError);
            if (unit is null)
                return StrategyCompileResult.Failed(
                    Append(diagnostics, Error("DAX1000", findError ?? "Could not bind the authored type.")));

            if (!unit.UsesRetiredContract)
            {
                // A sandbox unit is compiled, scanned and shaped — everything short of registration, which
                // still runs through the engine-era StrategyCatalogEntry and is the next slice of this
                // work. Reporting it as a success with the unit attached lets the verification ladder do
                // its job today instead of waiting for the registration path to be rebuilt.
                return StrategyCompileResult.CompiledWithoutRegistration(
                    diagnostics,
                    new AuthoredStrategyAssembly(image, assembly, unit.Type),
                    unit);
            }

            var option = BuildOption(script, assembly, out var kernelType, out var bindError);
            if (option is null || kernelType is null)
                return StrategyCompileResult.Failed(
                    Append(diagnostics, Error("DAX1000", bindError ?? "Could not bind the strategy type.")));

            // Discovered by the SDK's own finder — the same code the plugin loader runs on the next start,
            // so what the pane says is in the strategy and what actually loads can't disagree.
            var found = AuthoredStrategyTypes.DiscoverIn(assembly);
            var authored = new AuthoredStrategyAssembly(
                image, assembly, kernelType,
                DescriptorType: found.Descriptor,
                ViewModelType: found.ViewModel,
                ViewType: found.View);

            return StrategyCompileResult.Succeeded(option, diagnostics, authored, unit);
        }
        catch (Exception ex)
        {
            return StrategyCompileResult.Failed(
                Append(diagnostics, Error("DAX1001", $"Strategy load failed: {ex.Message}")));
        }
    }

    /// <summary>
    /// The plugin entry point, generated into every authored assembly. Without it the assembly is not a
    /// plugin at all: on the next start the loader finds the DLL, sees no <see cref="IStrategyPlugin"/>,
    /// and reports it as failed — which is exactly what happened before this existed.
    /// <para>
    /// It names no types of its own. The SDK's <c>AuthoredPluginBootstrap</c> discovers the kernel,
    /// descriptor, view-model and view by shape at load time, so this stays the same few lines whatever
    /// the author wrote.
    /// </para>
    /// </summary>
    private static string PluginEntryPoint(StrategyScript script) => $$"""
        /// <summary>Generated. Makes this authored strategy a real plugin, so the host loads it on the
        /// next start exactly like one built with `dotnet new daxalgo-strategy`.</summary>
        public sealed class DaxAlgoAuthoredPlugin : DaxAlgo.Sdk.IStrategyPlugin
        {
            public string Name => {{Literal(script.DisplayName)}};
            public string TargetSdkVersion => {{Literal(SdkInfo.Version)}};

            public void Register(DaxAlgo.Sdk.IPluginRegistrar registrar) =>
                DaxAlgo.Sdk.AuthoredPluginBootstrap.Register(
                    registrar,
                    typeof(DaxAlgoAuthoredPlugin).Assembly,
                    {{Literal(script.Id)}},
                    {{Literal(script.DisplayName)}});
        }
        """;

    /// <summary>A C# string literal — the id and display name are user input and land in generated source,
    /// so they are escaped rather than interpolated raw.</summary>
    private static string Literal(string value) =>
        Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Literal(value).ToFullString();

    /// <summary>
    /// Finds the one hostable type in a compiled submission.
    ///
    /// <para>Authoring targets <c>IStrategyKernel</c> and <c>IVisualizer</c>, so those are looked for
    /// first. <c>IOrderRoutedStrategy</c> is still accepted — <c>TradingTerminal.Core</c> is a published
    /// contract package and installed plugins implement it — but it is reported as the retired contract
    /// it is, rather than silently treated as current.</para>
    ///
    /// <para>Ambiguity is an error in both directions. A submission holding two kernels is unresolvable,
    /// and one holding both a kernel and a legacy strategy is a half-finished port, which is worse than
    /// either: picking one for the author would hide the fact that the other is dead code.</para>
    /// </summary>
    internal static AuthoredUnit? FindUnit(Assembly assembly, out string? error)
    {
        error = null;

        var classes = assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract).ToArray();
        var kernels = classes.Where(t => typeof(IStrategyKernel).IsAssignableFrom(t)).ToArray();
        var visualizers = classes.Where(t => typeof(IVisualizer).IsAssignableFrom(t)).ToArray();
        var legacy = classes.Where(t => typeof(IOrderRoutedStrategy).IsAssignableFrom(t)).ToArray();

        var total = kernels.Length + visualizers.Length + legacy.Length;
        if (total == 0)
        {
            error = "No public class implementing IStrategyKernel or IVisualizer was found. "
                  + "A strategy implements IStrategyKernel; a visualizer implements IVisualizer.";
            return null;
        }

        if (total > 1)
        {
            var found = string.Join(", ", kernels.Concat(visualizers).Concat(legacy).Select(t => t.Name));
            error = kernels.Length + visualizers.Length > 0 && legacy.Length > 0
                ? $"Found both a current and a retired contract ({found}). Keep the IStrategyKernel or "
                  + "IVisualizer implementation and delete the IOrderRoutedStrategy one."
                : $"Found {total} hostable classes ({found}); define exactly one.";
            return null;
        }

        if (kernels.Length == 1) return new AuthoredUnit(AuthoringKind.Strategy, kernels[0]);
        if (visualizers.Length == 1) return new AuthoredUnit(AuthoringKind.Visualizer, visualizers[0]);
        return new AuthoredUnit(AuthoringKind.Strategy, legacy[0], UsesRetiredContract: true);
    }

    /// <summary>Resolves the single <see cref="IOrderRoutedStrategy"/> class and wires its factory
    /// (and optional declarative-parameter members) into a <see cref="StrategyCatalogEntry"/>.</summary>
    private static StrategyCatalogEntry? BuildOption(
        StrategyScript script, Assembly assembly, out Type? kernelType, out string? error)
    {
        error = null;
        kernelType = null;
        var candidates = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IOrderRoutedStrategy).IsAssignableFrom(t))
            .ToArray();

        if (candidates.Length == 0)
        {
            error = "No public class implementing IOrderRoutedStrategy was found.";
            return null;
        }
        if (candidates.Length > 1)
        {
            error = $"Found {candidates.Length} IOrderRoutedStrategy classes; define exactly one " +
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
        Func<Contract, IOrderRoutedStrategy> build = contract =>
            (IOrderRoutedStrategy)ctor.Invoke(new object[] { contract });

        var schema = ReadStaticSchema(type);
        var parameterizedBuild = ReadParameterizedBuild(type);

        return new StrategyCatalogEntry(script.Id, script.DisplayName, build)
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

    /// <summary>Reads an optional <c>public static IOrderRoutedStrategy Create(Contract, StrategyParameters)</c>.</summary>
    private static Func<Contract, StrategyParameters, IOrderRoutedStrategy>? ReadParameterizedBuild(Type type)
    {
        var method = type.GetMethod(
            "Create", BindingFlags.Public | BindingFlags.Static, binder: null,
            types: new[] { typeof(Contract), typeof(StrategyParameters) }, modifiers: null);

        if (method is null || !typeof(IOrderRoutedStrategy).IsAssignableFrom(method.ReturnType))
            return null;

        return (contract, parameters) =>
            (IOrderRoutedStrategy)method.Invoke(null, new object[] { contract, parameters })!;
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

        // Core (IOrderRoutedStrategy, Contract, StrategyParameters, …) — belt and braces if it somehow
        // wasn't in the platform set.
        var core = typeof(IOrderRoutedStrategy).Assembly;
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

    /// <summary>
    /// Gives identical authored inputs an identical assembly identity while retaining the content suffix
    /// that lets a changed strategy coexist with the previous in-memory build during the same session.
    /// Length-prefixing each value makes the hash unambiguous (<c>["ab", "c"]</c> cannot collide with
    /// <c>["a", "bc"]</c> through concatenation alone).
    /// </summary>
    private static string BuildAssemblyName(StrategyScript script, string globals)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Append(script.Id);
        Append(script.DisplayName);
        Append(globals);
        Append(PluginEntryPoint(script));
        foreach (var file in script.Files)
        {
            Append(FileName(file, script));
            Append(file.Content);
        }

        var suffix = Convert.ToHexString(hash.GetHashAndReset())[..24].ToLowerInvariant();
        return $"DaxAlgo.Authored.{Sanitize(script.Id)}.{suffix}";

        void Append(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }
}
