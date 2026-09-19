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
    /// <summary>
    /// Imported into every unit, so a missing using is never the reason a build fails.
    ///
    /// <para><c>System.Text</c>, <c>System.Net.WebSockets</c> and <c>System.Globalization</c> joined on
    /// 2026-09-19: the network block invites a WebSocket, a WebSocket needs <c>Encoding.UTF8</c>, and the
    /// OpenRouter Nex N2.5 Pro run failed on <c>'Encoding' does not exist</c> in fourteen places. None of
    /// their type names collides with a Blocks, Quant or Core type.</para>
    /// </summary>
    public static IReadOnlyList<string> AmbientNamespaces { get; } =
    [
        "System",
        "System.Collections.Generic",
        "System.Linq",
        "System.Globalization",
        "System.Net.Http",
        "System.Net.WebSockets",
        "System.Text",
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
            .Select(d => Map(d, compilation)));

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

    private static StrategyDiagnostic Map(Diagnostic diagnostic, Compilation compilation)
    {
        var span = diagnostic.Location.GetLineSpan();
        return new StrategyDiagnostic(
            diagnostic.Severity == DiagnosticSeverity.Error ? StrategyDiagnosticSeverity.Error : StrategyDiagnosticSeverity.Warning,
            diagnostic.Id,
            diagnostic.GetMessage() + Hint(diagnostic, compilation),
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            File: span.Path ?? string.Empty);
    }

    /// <summary>
    /// What the compiler's sentence leaves out: the right name, beside the wrong one.
    ///
    /// <para><b>Every one of these was a repair loop on 2026-09-18/19.</b> A model told only what is wrong
    /// guesses again — <c>Settings.Enum</c> then <c>Settings.Choice</c>; <c>Trade</c> for
    /// <c>TradePrint</c>; <c>using DaxAlgo.Blocks.Math</c> for a block called <c>math.orderflow</c>;
    /// <c>LiquidationEvent</c> declared in two files by two builders. Each hint names the fix.</para>
    /// </summary>
    private static string Hint(Diagnostic diagnostic, Compilation compilation) => diagnostic.Id switch
    {
        "CS1061" or "CS0117" => RealMembers(diagnostic, compilation),
        "CS0234" or "CS0246" when InUsing(diagnostic) is { } imported => NotANamespace(imported),
        "CS0246" => SimilarTypes(diagnostic, compilation),
        "CS0101" => DeclaredIn(diagnostic, compilation),
        "CS0104" => " — the SDK already has a type with this name, and every SDK type is imported: rename "
                    + "yours, or use the SDK's.",
        _ => string.Empty,
    };

    /// <summary>The namespace a failing <c>using</c> names, or null when the error is not in one.</summary>
    private static string? InUsing(Diagnostic diagnostic)
    {
        if (diagnostic.Location.SourceTree is not { } tree) return null;
        var node = tree.GetRoot().FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        return node.AncestorsAndSelf().OfType<UsingDirectiveSyntax>().FirstOrDefault()?.Name?.ToString();
    }

    private static string NotANamespace(string imported) =>
        imported.StartsWith("DaxAlgo", StringComparison.Ordinal) || imported.StartsWith("TradingTerminal", StringComparison.Ordinal)
            ? " — block ids (market, math.orderflow …) are names of cards, not namespaces. Every Blocks, maths and "
              + "market type is already imported: delete this using."
            : " — delete this using; what a unit may call is already imported.";

    /// <summary>
    /// For "The type or namespace name 'Trade' could not be found": the imported types it was probably
    /// meant to be — " — did you mean TradePrint, TradeSide?".
    /// </summary>
    private static string SimilarTypes(Diagnostic diagnostic, Compilation compilation)
    {
        if (QuotedName(diagnostic.GetMessage()) is not { Length: >= 3 } missing) return string.Empty;

        var candidates = AmbientNamespaces
            .Select(ns => ns.Split('.').Aggregate<string, INamespaceSymbol?>(
                compilation.GlobalNamespace, (at, part) => at?.GetNamespaceMembers().FirstOrDefault(n => n.Name == part)))
            .Where(ns => ns is not null && !ns.ToDisplayString().StartsWith("System", StringComparison.Ordinal))
            .SelectMany(ns => ns!.GetTypeMembers())
            .Where(t => t.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public)
            .Select(t => (t.Name, Namespace: t.ContainingNamespace.ToDisplayString()))
            .DistinctBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => (t.Name, Score: Similarity(missing, t.Name),

                // Among equally close names, the market's own types and the blocks first: a unit asking
                // for "Trade" wants the print it is handed, not a maths helper that shares a prefix.
                Rank: t.Namespace is "TradingTerminal.Core.MarketData" or "DaxAlgo.Blocks" ? 0 : 1))
            .Where(c => c.Score < int.MaxValue)
            .OrderBy(c => c.Score)
            .ThenBy(c => c.Rank)
            .ThenBy(c => c.Name.Length)
            .Take(4)
            .Select(c => c.Name)
            .ToArray();

        return candidates.Length == 0
            ? string.Empty
            : $" — did you mean {string.Join(", ", candidates)}? Every Blocks, maths and market type is already imported.";
    }

    /// <summary>Lower is closer; <see cref="int.MaxValue"/> is unrelated.</summary>
    internal static int Similarity(string missing, string candidate)
    {
        if (candidate.StartsWith(missing, StringComparison.OrdinalIgnoreCase)) return 0;
        if (candidate.Contains(missing, StringComparison.OrdinalIgnoreCase)) return 1;
        if (candidate.Length >= 4 && missing.Contains(candidate, StringComparison.OrdinalIgnoreCase)) return 2;

        var distance = Levenshtein(missing.ToLowerInvariant(), candidate.ToLowerInvariant());
        return distance <= 2 ? 2 + distance : int.MaxValue;
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>
    /// For "already contains a definition for 'LiquidationEvent'": every file that declares it, and what
    /// to do about it.
    /// </summary>
    private static string DeclaredIn(Diagnostic diagnostic, Compilation compilation)
    {
        if (QuotedName(diagnostic.GetMessage(), last: true) is not { Length: > 0 } name) return string.Empty;

        var files = compilation.GetSymbolsWithName(name, SymbolFilter.Type)
            .SelectMany(s => s.Locations)
            .Where(l => l.IsInSource)
            .Select(l => l.SourceTree!.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return files.Length < 2
            ? string.Empty
            : $" — '{name}' is declared in {string.Join(" and ", files)}. Keep ONE: a type belongs to the file the "
              + "contract gives it; a small record only your file uses belongs NESTED inside your own class.";
    }

    /// <summary>The first (or last) 'quoted' name in a compiler message.</summary>
    private static string? QuotedName(string message, bool last = false)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(message, "'([A-Za-z_][A-Za-z0-9_]*)'");
        return matches.Count == 0 ? null : matches[last ? ^1 : 0].Groups[1].Value;
    }

    /// <summary>What a type does have, beside the compiler saying what it does not.</summary>
    private static readonly HashSet<string> Boilerplate = new(StringComparer.Ordinal)
    {
        "Equals", "GetHashCode", "ToString", "GetType", "Deconstruct", "PrintMembers", "EqualityContract",
    };

    /// <summary>
    /// For "'ISettings' does not contain a definition for 'Choice'" (CS1061, CS0117), the members the type
    /// really has — " — ISettings has: Int, Number, Bool, Text, …" — or nothing.
    ///
    /// <para><b>A model repairing a missing member guesses another name.</b> Measured 2026-09-18 on the
    /// free DeepSeek V4 Flash: <c>Settings.Enum</c>, then <c>Settings.Choice</c>, then
    /// <c>TradePrint.TimeUtc</c> — ten repair rounds over two runs, with the settings card that names
    /// <c>Text()</c> in the very prompt. The compiler's sentence says only what is wrong; the list says
    /// what is right, for SDK types and the unit's own alike.</para>
    /// </summary>
    private static string RealMembers(Diagnostic diagnostic, Compilation compilation)
    {
        if (diagnostic.Id is not ("CS1061" or "CS0117") || diagnostic.Location.SourceTree is not { } tree)
            return string.Empty;

        var node = tree.GetRoot().FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        var receiver = node.AncestorsAndSelf().OfType<MemberAccessExpressionSyntax>().FirstOrDefault()?.Expression;
        if (receiver is null) return string.Empty;

        var model = compilation.GetSemanticModel(tree);
        var type = model.GetTypeInfo(receiver).Type ?? model.GetSymbolInfo(receiver).Symbol as ITypeSymbol;
        if (type is null or IErrorTypeSymbol) return string.Empty;

        // An enum is its values. Everything else is itself, its bases and its interfaces — but not what
        // .NET puts under every type (Object, ValueType, IComparable, IConvertible), which is noise here.
        var owners = new List<ITypeSymbol> { type };
        if (type.TypeKind != TypeKind.Enum)
        {
            owners.AddRange(type.AllInterfaces.Where(i => !IsDotNet(i)));
            for (var b = type.BaseType; b is not null && !IsDotNet(b); b = b.BaseType) owners.Add(b);
        }

        var names = owners
            .SelectMany(t => t.GetMembers())
            .Where(m => m.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public && m.CanBeReferencedByName && !Boilerplate.Contains(m.Name))
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .Take(40)
            .ToArray();

        return names.Length == 0 ? string.Empty : $" — {type.Name} has: {string.Join(", ", names)}.";
    }

    private static bool IsDotNet(ITypeSymbol type) =>
        type.ContainingNamespace?.ToDisplayString() is { } ns && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal));

    private static StrategyDiagnostic Error(string id, string message, string file = "") =>
        new(StrategyDiagnosticSeverity.Error, id, message, 0, 0, file);

    private static string Sanitize(string id) =>
        new(id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
