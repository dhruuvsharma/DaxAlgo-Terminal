using System.IO;
using System.Reflection;
using DaxAlgo.Blocks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Plugins;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;

/// <summary>What compiling a Blocks unit produced.</summary>
/// <param name="Success">Whether the unit compiled, passed the scan and was found.</param>
/// <param name="Diagnostics">Compiler errors and warnings, and every scan finding.</param>
/// <param name="UnitType">The class implementing <see cref="IUnit"/>.</param>
/// <param name="Factory">Makes a fresh instance of the unit.</param>
/// <param name="UsesOrders">True when the source uses the orders block — the unit is a strategy.</param>
/// <param name="PageFiles">The unit's web page files (<c>ui/…</c>), separated from the C#.</param>
/// <param name="Image">The compiled assembly, for packaging.</param>
public sealed record BlocksCompileResult(
    bool Success,
    IReadOnlyList<StrategyDiagnostic> Diagnostics,
    Type? UnitType = null,
    Func<IUnit>? Factory = null,
    bool UsesOrders = false,
    IReadOnlyList<StrategyFile>? PageFiles = null,
    byte[]? Image = null)
{
    public IEnumerable<StrategyDiagnostic> Errors => Diagnostics.Where(d => d.Severity == StrategyDiagnosticSeverity.Error);
}

/// <summary>
/// Compiles a unit written against DaxAlgo.Blocks: its C# files into an assembly, its <c>ui/</c> files
/// set aside as the page.
///
/// <para><b>It can only see the Blocks SDK.</b> References are the .NET base library, Core's data
/// records, the maths library and DaxAlgo.Blocks — not the WPF framework, not DaxAlgo.Sdk's drawing
/// surface and widgets, not the terminal's own assemblies. A unit that reaches for a widget does not
/// compile, which is a better answer than one that compiles and is refused later.</para>
///
/// <para><b>The network is allowed; nothing else new is.</b> The image is scanned with the sandbox
/// profile and only its network findings are set aside. Processes, the registry, P/Invoke, loading
/// assemblies, raw threads and every <c>System.IO</c> type stay refused. Streams go with files because
/// the scanner reports one finding per rule, so a permitted <c>Stream</c> would hide a forbidden
/// <c>File</c> behind it.</para>
/// </summary>
public sealed class BlocksUnitCompiler
{
    /// <summary>Imported into every unit, so a missing using is never the reason a build fails.</summary>
    public static IReadOnlyList<string> AmbientNamespaces { get; } =
    [
        "System",
        "System.Collections.Generic",
        "System.Linq",
        "System.Net.Http",
        "System.Text.Json",
        "System.Threading",
        "System.Threading.Tasks",
        "DaxAlgo.Blocks",
        "DaxAlgo.Sdk.Quant",
        "TradingTerminal.Core.Domain",
        "TradingTerminal.Core.MarketData",
        "TradingTerminal.Core.Strategies.Parameters",
    ];

    /// <summary>File types a unit's page may contain.</summary>
    public static IReadOnlyList<string> PageExtensions { get; } = [".html", ".htm", ".js", ".mjs", ".css", ".json", ".svg"];

    /// <summary>The largest single page file, in characters.</summary>
    public const int MaximumPageFileLength = 1_048_576;

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    public BlocksCompileResult Compile(string unitId, IReadOnlyList<StrategyFile> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        ArgumentNullException.ThrowIfNull(files);

        var diagnostics = new List<StrategyDiagnostic>();
        var sources = new List<StrategyFile>();
        var page = new List<StrategyFile>();

        foreach (var file in files)
        {
            var name = (file.Name ?? string.Empty).Replace('\\', '/').Trim();
            if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                sources.Add(file with { Name = name });
            }
            else if (PageFile(name, file.Content) is { } problem)
            {
                diagnostics.Add(Error("DAXB001", problem, name));
            }
            else
            {
                page.Add(file with { Name = name });
            }
        }

        if (sources.Count == 0)
            diagnostics.Add(Error("DAXB002", "A unit needs at least one C# file with a class implementing IUnit."));

        if (diagnostics.Count > 0) return new BlocksCompileResult(false, diagnostics, PageFiles: page);

        var globals = string.Join('\n', AmbientNamespaces.Select(ns => $"global using {ns};"));
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(globals, ParseOptions, path: "GlobalUsings.g.cs") };
        trees.AddRange(sources.Select(file => CSharpSyntaxTree.ParseText(file.Content, ParseOptions, path: file.Name)));

        var compilation = CSharpCompilation.Create(
            assemblyName: $"DaxAlgo.Unit.{Sanitize(unitId)}.{Guid.NewGuid():N}",
            syntaxTrees: trees,
            references: References(),
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Enable,
                allowUnsafe: false));

        using var image = new MemoryStream();
        var emit = compilation.Emit(image);
        diagnostics.AddRange(emit.Diagnostics
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(Map));

        if (!emit.Success) return new BlocksCompileResult(false, diagnostics, PageFiles: page);

        var bytes = image.ToArray();
        var scan = Scan(bytes, unitId);
        diagnostics.AddRange(scan.Findings.Select(finding => new StrategyDiagnostic(
            finding.Severity == PluginScanSeverity.Block ? StrategyDiagnosticSeverity.Error : StrategyDiagnosticSeverity.Warning,
            $"DAXSCAN:{finding.Rule}",
            finding.Detail,
            0,
            0)));

        if (scan.Verdict == PluginScanSeverity.Block) return new BlocksCompileResult(false, diagnostics, PageFiles: page);

        Assembly assembly;
        try
        {
            assembly = Assembly.Load(bytes);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
        {
            diagnostics.Add(Error("DAXB003", $"The compiled unit could not be loaded: {ex.Message}"));
            return new BlocksCompileResult(false, diagnostics, PageFiles: page);
        }

        var candidates = assembly.GetExportedTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IUnit).IsAssignableFrom(type))
            .ToArray();

        if (candidates.Length != 1)
        {
            diagnostics.Add(Error("DAXB004", candidates.Length == 0
                ? "No public class implementing IUnit was found."
                : $"Exactly one public class may implement IUnit; found {string.Join(", ", candidates.Select(c => c.Name))}."));
            return new BlocksCompileResult(false, diagnostics, PageFiles: page);
        }

        var unitType = candidates[0];
        if (unitType.GetConstructor(Type.EmptyTypes) is null)
        {
            diagnostics.Add(Error("DAXB005", $"'{unitType.Name}' needs a public parameterless constructor."));
            return new BlocksCompileResult(false, diagnostics, PageFiles: page);
        }

        if (page.Count > 0 && !page.Any(f => f.Name.Equals("ui/index.html", StringComparison.OrdinalIgnoreCase)))
            diagnostics.Add(new StrategyDiagnostic(StrategyDiagnosticSeverity.Warning, "DAXB006",
                "The unit has page files but no ui/index.html, so its window will have nothing to show.", 0, 0));

        return new BlocksCompileResult(
            true,
            diagnostics,
            unitType,
            () => (IUnit)Activator.CreateInstance(unitType)!,
            UsesOrders(compilation),
            page,
            bytes);
    }

    /// <summary>
    /// The sandbox scan without its network findings — the whole of what the Hyperion profile relaxes.
    /// </summary>
    public static PluginScanReport Scan(byte[] image, string unitId)
    {
        var sandbox = PluginPolicyScanner.ScanSandboxImage(image, $"{Sanitize(unitId)}.dll");
        var kept = sandbox.Findings.Where(finding => finding.Rule != "network").ToArray();
        var verdict = kept.Length == 0 ? PluginScanSeverity.Clean : kept.Max(finding => finding.Severity);
        return new PluginScanReport(verdict, kept);
    }

    /// <summary>
    /// Whether the source touches the orders block, read off the compiler's own symbols rather than a
    /// text search, so a comment mentioning orders does not make a visualizer a strategy.
    /// </summary>
    internal static bool UsesOrders(CSharpCompilation compilation)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                switch (model.GetSymbolInfo(name).Symbol)
                {
                    case IPropertySymbol { Name: nameof(IUnitContext.Orders) } property
                        when property.ContainingType.ToDisplayString() == typeof(IUnitContext).FullName:
                        return true;
                    case IMethodSymbol method
                        when method.ContainingType.ToDisplayString() == typeof(IOrders).FullName:
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The base library, Core's records, the maths library and the Blocks SDK — and nothing else.
    /// </summary>
    private static IReadOnlyList<MetadataReference> References()
    {
        var framework = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // Only the shared .NET runtime folder: WPF and WinForms live in a different one.
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(Path.GetDirectoryName(path), framework, StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(Path.GetFileNameWithoutExtension(path))) references.Add(MetadataReference.CreateFromFile(path));
        }

        foreach (var assembly in new[]
                 {
                     typeof(TradingTerminal.Core.Domain.InstrumentId).Assembly,
                     typeof(DaxAlgo.Sdk.Quant.Num).Assembly,
                     typeof(IUnit).Assembly,
                 })
        {
            if (seen.Add(assembly.GetName().Name!)) references.Add(MetadataReference.CreateFromFile(assembly.Location));
        }

        return references;
    }

    private static string? PageFile(string name, string? content)
    {
        if (!name.StartsWith("ui/", StringComparison.OrdinalIgnoreCase))
            return $"'{name}' is neither C# nor a page file; page files live under ui/.";
        if (name.Split('/').Any(segment => segment is "" or "." or ".."))
            return $"'{name}' is not a plain relative path.";
        if (!PageExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            return $"'{name}' is not a page file type ({string.Join(", ", PageExtensions)}).";
        if ((content?.Length ?? 0) > MaximumPageFileLength)
            return $"'{name}' is larger than {MaximumPageFileLength:N0} characters.";
        return null;
    }

    private static StrategyDiagnostic Map(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return new StrategyDiagnostic(
            diagnostic.Severity == DiagnosticSeverity.Error ? StrategyDiagnosticSeverity.Error : StrategyDiagnosticSeverity.Warning,
            diagnostic.Id,
            diagnostic.GetMessage(),
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            File: span.Path ?? string.Empty);
    }

    private static StrategyDiagnostic Error(string id, string message, string file = "") =>
        new(StrategyDiagnosticSeverity.Error, id, message, 0, 0, file);

    private static string Sanitize(string id) =>
        new(id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
