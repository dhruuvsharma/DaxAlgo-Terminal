using System.IO;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using DaxAlgo.Sdk;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Emits the authoring contract surface a model is given, <b>reflected from
/// <c>DaxAlgo.Sdk</c></b> rather than described by hand.
///
/// <para>This exists because the hand-maintained pack drifted badly: it taught
/// <c>IBacktestStrategy</c> nine times, <c>IStrategyKernel</c> once, and said nothing whatsoever
/// about <c>IRenderSurface</c> or <c>Draw</c> — so the one contract that puts pixels on screen was
/// invisible to the thing meant to generate against it. <c>StrategyContextPack</c>'s own doc records
/// how that happened: its generator, <c>build/gen-ai-context.ps1</c>, went missing and the markdown
/// was left to be maintained by hand.</para>
///
/// <para>The fix is to derive the facts and hand-write only the judgement. Signatures and summaries
/// come from the assembly and its XML documentation, so a contract change shows up here on the next
/// build; <c>SdkSurfaceFreshnessTests</c> fails when the committed copy falls behind. Prose about
/// <i>how to think</i> about the contracts stays hand-written, where it belongs.</para>
/// </summary>
public static class SdkSurfaceGenerator
{
    /// <summary>Where the generated surface is committed, relative to the repository root.</summary>
    public const string RelativePath = "sdk/ai-context/generated/sdk-surface.md";

    /// <summary>
    /// The authoring surface, in the order an author meets it: what you implement, what you draw
    /// onto, then the helpers and the vocabulary. Types outside this list are host wiring an author
    /// never touches, and including them would spend a model's attention on noise.
    /// </summary>
    public const string ImplementSection = "What you implement";
    public const string DrawOntoSection = "What you draw onto";
    public const string QuantSection = "Quant helpers";
    public const string DrawingSection = "Drawing helpers";
    public const string VocabularySection = "Vocabulary";

    private static readonly string[] Sections =
    [
        ImplementSection,
        DrawOntoSection,
        QuantSection,
        DrawingSection,
        VocabularySection,
    ];

    /// <summary>The sections in the order they are written.</summary>
    public static IReadOnlyList<string> SectionOrder => Sections;

    /// <summary>
    /// The sections a brief may be given in part rather than whole — the two <i>libraries</i>.
    ///
    /// <para>The other three are contracts: what you implement, what you draw onto, and the vocabulary
    /// those two are written in. A unit that is not shown its own interface cannot be written at all,
    /// so those are never rationed however long the brief is. A unit not shown <c>KalmanHedgeRatio</c>
    /// writes a slightly worse strategy, which is a cost worth paying to reach the model at all.</para>
    /// </summary>
    public static IReadOnlyList<string> HelperSections => [QuantSection, DrawingSection];

    /// <summary>
    /// Public types an author never names, and which are therefore left out entirely.
    ///
    /// <para>The section list above already says the intent — "types outside this list are host
    /// wiring an author never touches, and including them would spend a model's attention on noise" —
    /// but "Vocabulary" is a fallback that catches everything unmatched, so the intent was never
    /// enforced and every one of these shipped at full length in the system prompt.</para>
    ///
    /// <para>Three kinds of noise: the plugin entry points, which the compiler generates rather than
    /// the author (<c>AuthoredPluginBootstrap</c>, <c>IStrategyPlugin</c>, <c>IPluginRegistrar</c>,
    /// <c>PluginContext</c>, <c>AuthoredStrategyTypes</c>); the lifecycle interfaces, which the host
    /// implements and calls; and the surfaces that exist for tests (<c>RecordingRenderSurface</c>,
    /// <c>NullRenderSurface</c> and their records). Documenting a recording surface to a model
    /// authoring a strategy invites it to construct one.</para>
    /// </summary>
    private static readonly HashSet<string> HostWiring =
    [
        nameof(AuthoredPluginBootstrap),
        nameof(AuthoredStrategyTypes),
        nameof(IPluginRegistrar),
        nameof(IStrategyEngineFactory),
        nameof(IStrategyLifecycle),
        nameof(IStrategyPlugin),
        nameof(IVisualizerLifecycle),
        nameof(NullRenderSurface),
        nameof(PluginContext),
        nameof(RecordedLine),
        nameof(RecordedRect),
        nameof(RecordedText),
        nameof(RecordingRenderSurface),
        nameof(RenderCall),
    ];

    public static string Generate()
    {
        var assembly = typeof(IStrategyKernel).Assembly;
        var docs = XmlDocumentation.LoadFor(assembly, typeof(TradingTerminal.Core.Domain.InstrumentId).Assembly);

        var markdown = new StringBuilder();
        markdown.AppendLine("<!-- GENERATED by SdkSurfaceGenerator. Do not edit by hand.");
        markdown.AppendLine("     Reflected from DaxAlgo.Sdk so it cannot drift from the contracts it describes.");
        markdown.AppendLine("     Regenerate by running SdkSurfaceFreshnessTests. -->");
        markdown.AppendLine();
        markdown.AppendLine("# DaxAlgo SDK — the authoring surface");
        markdown.AppendLine();
        var version = SdkInfo.Version;
        markdown.AppendLine(
            "Every public member below is available to an authored strategy or visualizer. "
            + $"SDK {version}.");
        markdown.AppendLine();

        var sdkTypes = assembly.GetExportedTypes()
            .Where(t => !t.IsNested && !HostWiring.Contains(t.Name))
            .ToArray();

        var types = sdkTypes
            .Select(type => (Type: type, Section: Section(type)))
            .Concat(HandedTo(sdkTypes))
            .ToLookup(pair => pair.Section, pair => pair.Type);

        foreach (var section in Sections)
        {
            var members = types[section].OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
            if (members.Length == 0) continue;

            markdown.AppendLine($"## {section}");
            markdown.AppendLine();
            foreach (var type in members)
            {
                // A boundary marker before every type, so the document can be split back into its
                // types at runtime without parsing markdown. SdkSurfaceSelector reads these to decide
                // which types a brief gets in full and which it gets one line of; splitting on "### "
                // instead would break the moment a summary contained a fenced heading, and would have
                // no way to know a type's section or its search terms.
                //
                // A comment, so it costs the model nothing and cannot be mistaken for content.
                markdown.AppendLine(Marker(type, section));
                AppendType(markdown, type, section, docs, handedIn: !sdkTypes.Contains(type));
            }
        }

        return markdown.ToString();
    }

    /// <summary>
    /// The <c>TradingTerminal.Core</c> types the SDK's own signatures hand to a unit — and which were
    /// therefore named in this document without ever being defined in it.
    ///
    /// <para><b>Found by a live run that would not compile.</b> A footprint brief reached for
    /// <c>FeedQuality.Partial</c>, a member that does not exist on a type it was never shown. The
    /// systematic version is worse: <c>OhlcvBar</c>, <c>Quote</c>, <c>TradePrint</c>,
    /// <c>DepthSnapshot</c> and <c>FootprintBar</c> were all absent, so a unit was told
    /// <c>OnBarAsync(OhlcvBar bar, …)</c> and never what an <c>OhlcvBar</c> holds. It went unnoticed
    /// because <c>bar.Close</c> and <c>quote.Bid</c> are guessable; <c>FootprintBar</c> is not, and
    /// <c>Footprint.Draw</c> takes a list of them.</para>
    ///
    /// <para><b>Derived from the signatures rather than listed</b>, which is the property this whole
    /// generator exists for. A type that stops appearing in a contract stops being taught, and a new
    /// one starts, with nobody maintaining a roster.</para>
    ///
    /// <para><b>To a fixed point, because printing a type prints its members.</b> One pass put
    /// <c>DepthSnapshot</c> in the vocabulary and left <c>DepthLevel</c> out of it — and
    /// <c>DepthSnapshot.Bids</c> is a list of them, so the document would once again have named a type
    /// it never defined, which is the exact defect this method was written to close. The closure is
    /// over what is <i>printed</i>, not over what is reachable: only public, non-nested Core types
    /// named by a member this document actually emits, which is why it settles in a handful of rounds
    /// instead of dragging in Core entire.</para>
    /// </summary>
    /// <param name="sdkTypes">The types already being printed, and where the walk starts.</param>
    /// <returns>Each handed-in type with the section it belongs in.</returns>
    private static IEnumerable<(Type Type, string Section)> HandedTo(IReadOnlyCollection<Type> sdkTypes)
    {
        var core = typeof(TradingTerminal.Core.Domain.InstrumentId).Assembly;

        // Where each type was referenced FROM, because that is what decides whether every prompt pays
        // for it. A type only a widget mentions — FootprintBar, at 2,000 characters — belongs in the
        // rationed drawing library beside the widget that takes it; a type a CONTRACT mentions is
        // vocabulary and is never cut, because every unit's callbacks receive one.
        var referrers = new Dictionary<Type, HashSet<string>>();

        // Walked per (type, SECTION) rather than per type. A type first reached from the quant library
        // and later from a contract has to be walked again, or the types IT names keep the rationed
        // placement of the first path that happened to find it.
        var seen = new HashSet<(Type, string)>();
        var frontier = sdkTypes.Select(type => (Type: type, Section: Section(type))).ToList();
        foreach (var start in frontier) seen.Add(start);

        while (frontier.Count > 0)
        {
            var next = new List<(Type Type, string Section)>();

            foreach (var (owner, from) in frontier)
            {
                foreach (var referenced in Members(owner).SelectMany(Mentions))
                {
                    var bare = Unwrap(referenced);
                    if (bare.Assembly != core || bare.IsNested || !bare.IsPublic) continue;
                    if (HostWiring.Contains(bare.Name)) continue;

                    if (!referrers.TryGetValue(bare, out var sections))
                        referrers[bare] = sections = new HashSet<string>(StringComparer.Ordinal);

                    // A type inherits its referrer's placement, so the whole of a vocabulary type's
                    // own vocabulary stays unrationed with it rather than scattering into the
                    // libraries that happened to mention it first.
                    sections.Add(from);
                    if (seen.Add((bare, from))) next.Add((bare, from));
                }
            }

            frontier = next;
        }

        return referrers
            .Select(entry => (
                Type: entry.Key,
                // Rationed only when EVERY referrer is a helper section. One contract mention is enough
                // to make it vocabulary: a type a callback hands you cannot be allowed to disappear
                // because the brief did not happen to name it.
                Section: entry.Value.All(HelperSections.Contains) && entry.Value.Count == 1
                    ? entry.Value.Single()
                    : Sections[4]))
            .OrderBy(pair => pair.Type.Name, StringComparer.Ordinal);
    }

    /// <summary>How many characters of commentary this type would contribute — its own summary plus one
    /// per member, which for an enum means one per named value. Measured rather than guessed from the
    /// member count, because the cost is the prose and not the roster.</summary>
    private static int Prose(Type type, XmlDocumentation docs)
    {
        var keys = type.IsEnum
            ? Enum.GetNames(type).Select(name => $"F:{type.FullName}.{name}")
            : Members(type).Select(MemberKey);

        return (docs.Summary(MemberKey(type))?.Length ?? 0)
            + keys.Sum(key => docs.Summary(key)?.Length ?? 0);
    }

    /// <summary>The members this document prints for a type — the same set <c>AppendType</c> emits, so
    /// what is scanned for referenced types is exactly what a reader is shown.</summary>
    private static IEnumerable<MemberInfo> Members(Type type) => type
        .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Where(IsInteresting);

    /// <summary>Every type a member's signature names — its return and its parameters.</summary>
    private static IEnumerable<Type> Mentions(MemberInfo member) => member switch
    {
        MethodInfo method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType),
        PropertyInfo property => [property.PropertyType],
        ConstructorInfo constructor => constructor.GetParameters().Select(p => p.ParameterType),
        _ => [],
    };

    /// <summary>Peels a by-ref, an array, a nullable or a one-argument generic down to the type that
    /// actually needs describing — <c>IReadOnlyList&lt;FootprintBar&gt;</c> is a list, and what a unit
    /// does not know is the bar.</summary>
    private static Type Unwrap(Type type)
    {
        if (type.IsByRef || type.IsArray) return Unwrap(type.GetElementType()!);
        if (type.IsGenericType)
        {
            var arguments = type.GetGenericArguments();
            if (arguments.Length == 1) return Unwrap(arguments[0]);
        }
        return type;
    }

    /// <summary>The prefix every type marker starts with.</summary>
    public const string MarkerPrefix = "<!-- @type ";

    /// <summary>
    /// The boundary line before one type: its name and its section, and nothing else.
    ///
    /// <para><b>Deliberately just the boundary.</b> The first version carried the search terms too —
    /// the name and members split on camel case plus the lead summary's words — which was 26 KB of
    /// them across the library. That is a quarter of the document, embedded in the assembly, and sent
    /// verbatim to the model on any path that does not cut. A mechanism built to shrink the prompt had
    /// grown it. <see cref="SdkSurfaceSelector"/> derives the same terms from the block it is already
    /// holding, so nothing is lost and nothing is duplicated.</para>
    ///
    /// <para>Markers never reach a model: the selector consumes them while parsing, and returns the
    /// document without them however much of it a brief earns.</para>
    /// </summary>
    private static string Marker(Type type, string section) =>
        $"{MarkerPrefix}{type.Name} | {section} -->";

    /// <summary>Writes the surface to <paramref name="repositoryRoot"/>, creating the folder. Returns
    /// true when the bytes changed, so a caller can tell "regenerated" from "already current".</summary>
    public static bool WriteTo(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var generated = Generate();
        // Compare normalised, so a checkout with different line endings is not perpetually "stale".
        if (File.Exists(path) && Normalize(File.ReadAllText(path)) == Normalize(generated))
            return false;

        File.WriteAllText(path, generated);
        return true;
    }

    public static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();

    private static string Section(Type type)
    {
        if (type == typeof(IStrategyKernel) || type == typeof(IVisualizer))
            return Sections[0];
        if (type == typeof(IRenderSurface))
            return Sections[1];
        if (type.Namespace == "DaxAlgo.Sdk.Quant")
            return Sections[2];
        if (type.Namespace == "DaxAlgo.Sdk.Drawing")
            return Sections[3];
        return Sections[4];
    }

    /// <summary>
    /// An options record: a struct carrying a static <c>Default</c> of its own type.
    ///
    /// <para>Detected structurally rather than by the <c>Options</c> suffix, so the rule holds for a type
    /// somebody names differently and does not fire on a type that merely ends that way.</para>
    /// </summary>
    private static bool IsOptionsRecord(Type type) =>
        type.IsValueType
        && type.GetProperty("Default", BindingFlags.Public | BindingFlags.Static) is { } d
        && d.PropertyType == type
        // AND it must have no behaviour, which the first version did not check.
        //
        // Camera3 matched: a struct with a static Default. So it was compacted to one line of field
        // names and BOTH its methods were dropped -- Orbit, which is how a scene animates, and
        // Framing, which is how a scene is aimed at its data. Neither has ever appeared in the prompt.
        // That is a plain answer to why no generated 3D unit has ever spun or fitted its camera: it
        // was never shown either call, while the drawing pack talked about animation.
        //
        // An options record is DATA -- fields and a sane Default. A type with methods is a tool, and a
        // tool has to show them.
        && !Members(type).OfType<MethodInfo>().Any();

    /// <summary>
    /// The prose budget for a type reflected out of <c>TradingTerminal.Core</c>.
    ///
    /// <para><b>Because the SDK's doc comments are prompt copy and Core's are not.</b> Every summary in
    /// <c>DaxAlgo.Sdk</c> is written knowing a model will read it. Core is documented for the people who
    /// maintain Core — so <c>BrokerKind</c> arrived spending 4,500 unrationed characters on 47 venue
    /// names, one of which explains at length why an obsolete member cannot be deleted without
    /// renumbering everyone's stored history. True, useful, and addressed to a maintainer.</para>
    ///
    /// <para>Past this, a handed-in type keeps its lead sentence and every member NAME and SIGNATURE —
    /// which is the whole of what stops a model inventing <c>FeedQuality.Partial</c> — and drops the
    /// per-member commentary. Small ones keep everything, because <c>OhlcvBar</c> explaining the
    /// difference between its event time and its ingest time is worth its 700 characters.</para>
    /// </summary>
    private const int HandedInProseBudget = 1200;

    private static void AppendType(
        StringBuilder markdown, Type type, string section, XmlDocumentation docs, bool handedIn = false)
    {
        // Options records are compacted to one line each.
        //
        // The widget library nearly doubled this document — and this document is the system prompt, paid
        // for on the first turn of every session and re-read from cache on all the others, including
        // sessions for a headless kernel that draws nothing. Spelling out every field of every options
        // record, twice, would have cost more per session than the widgets save in generated code, which
        // would make a library built to reduce token burn increase it.
        //
        // What a model needs in order to CALL a widget is the Draw signature and the knowledge that an
        // options record exists with a sane Default. What each field means is what the drawing skill is
        // for, and the skill is loaded only when the brief is about a picture.
        if (IsOptionsRecord(type))
        {
            var fields = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(p => p.Name)
                .OrderBy(name => name, StringComparer.Ordinal);

            markdown.AppendLine(
                $"- `{TypeName(type)}` — {Inline(Lead(docs.Summary(MemberKey(type))) ?? "Options.")} "
                + $"Fields: {string.Join(", ", fields)}. Use `{TypeName(type)}.Default`, never `new()`.");
            markdown.AppendLine();
            return;
        }

        markdown.AppendLine($"### `{TypeName(type)}`");
        markdown.AppendLine();

        // Widgets and estimators get their lead paragraph only. There are a lot of them, each with
        // several paragraphs of rationale, and rationale is what the on-demand skills are for — loaded
        // when the brief calls for a picture or for maths, rather than carried by every session
        // including the ones that need neither. The contracts an author must not get wrong keep their
        // full text.
        // The RESOLVED section, passed in rather than recomputed. A handed-in type sits where its
        // referrer put it, and Section() only knows about the SDK's own namespaces — so recomputing
        // here would file FootprintBar under the drawing library and then print it at vocabulary
        // length, which is the 2,000 characters this was meant to ration.
        var summary = docs.Summary(MemberKey(type));
        if (section == Sections[2] || section == Sections[3]) summary = Lead(summary);

        var terse = handedIn && Prose(type, docs) > HandedInProseBudget;
        if (terse) summary = Lead(summary);

        if (summary is not null)
        {
            markdown.AppendLine(summary.Replace(
                XmlDocumentation.ParagraphMark.ToString(), "\n\n", StringComparison.Ordinal));
            markdown.AppendLine();
        }

        if (type.IsEnum)
        {
            foreach (var name in Enum.GetNames(type))
            {
                var value = terse ? null : docs.Summary($"F:{type.FullName}.{name}");
                markdown.AppendLine(value is null ? $"- `{name}`" : $"- `{name}` — {Inline(value)}");
            }
            markdown.AppendLine();
            return;
        }

        // An estimator's Value / IsReady / Reset are the IEstimator contract, documented once on the
        // interface. Repeating all three on twenty-odd types costs sixty lines of the system prompt to
        // say the same thing twenty times, and buries the members that actually differ between them.
        var isEstimator = typeof(DaxAlgo.Sdk.Quant.IEstimator).IsAssignableFrom(type)
            && type != typeof(DaxAlgo.Sdk.Quant.IEstimator);

        var members = type
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(IsInteresting)
            .Where(m => !isEstimator || m.Name is not ("Value" or "IsReady" or "Reset"))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToArray();

        if (isEstimator)
        {
            markdown.AppendLine("Implements `IEstimator`: `Value`, `IsReady`, `Reset()`.");
            markdown.AppendLine();
        }

        if (members.Length == 0) return;

        markdown.AppendLine("```csharp");
        foreach (var member in members) markdown.AppendLine(Signature(member));
        markdown.AppendLine("```");
        markdown.AppendLine();

        if (!terse)
        {
            foreach (var member in members)
            {
                var text = docs.Summary(MemberKey(member));
                if (text is not null) markdown.AppendLine($"- `{member.Name}` — {Inline(text)}");
            }
        }
        markdown.AppendLine();
    }

    /// <summary>The first sentence of a summary — enough to say what a type is for, without the
    /// paragraphs of rationale that belong in the skill packs rather than the always-on prefix.</summary>
    /// <summary>A summary flattened onto one line — for a bullet, which cannot hold a paragraph.</summary>
    private static string Inline(string text) => text.Replace(XmlDocumentation.ParagraphMark, ' ');

    private static string? Lead(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return null;

        var stop = summary.IndexOf(XmlDocumentation.ParagraphMark);
        return (stop < 0 ? summary : summary[..stop]).Trim();
    }

    private static bool IsInteresting(MemberInfo member) => member switch
    {
        // Property accessors and compiler-generated record plumbing would triple the size of this
        // document while teaching a model nothing it can act on.
        MethodInfo method => !method.IsSpecialName
            && method.Name is not ("ToString" or "GetHashCode" or "Equals" or "Deconstruct" or "<Clone>$"),
        PropertyInfo => true,

        // How to MAKE one, which for a positional record is the whole of its shape and was missing.
        //
        // Found by the run that proved the previous fix: teaching FootprintBar's members stopped the
        // invented enum member and then failed on "'FootprintBar' does not contain a constructor that
        // takes 15 arguments", and on an object initializer against get-only properties. A unit is
        // handed raw TradePrints and Footprint.Draw wants FootprintBars, so building them is not
        // optional — and every property was printed with no way to learn the constructor.
        //
        // Mentions() has had a ConstructorInfo arm the whole time. This filter ran first, so nothing
        // could reach it.
        ConstructorInfo constructor => constructor.IsPublic,

        _ => false,
    };

    private static string Signature(MemberInfo member) => member switch
    {
        PropertyInfo p => $"{TypeName(p.PropertyType)} {p.Name} {{ get; }}",
        MethodInfo m =>
            $"{TypeName(m.ReturnType)} {m.Name}("
            + string.Join(", ", m.GetParameters().Select(Parameter))
            + ")",
        // Written as the call, not the declaration, because the call is what a unit has to get right.
        ConstructorInfo c =>
            $"new {TypeName(c.DeclaringType!)}("
            + string.Join(", ", c.GetParameters().Select(Parameter))
            + ")",
        _ => member.Name,
    };

    private static string Parameter(ParameterInfo p)
    {
        var text = $"{TypeName(p.ParameterType)} {p.Name}";
        // Defaults are part of the contract: they tell a model which arguments it may leave out.
        if (!p.HasDefaultValue) return text;
        return $"{text} = {p.DefaultValue switch { null => "null", bool b => b ? "true" : "false", var v => v.ToString() }}";
    }

    private static string TypeName(Type type)
    {
        if (type == typeof(void)) return "void";
        if (Nullable.GetUnderlyingType(type) is { } inner) return TypeName(inner) + "?";

        if (type.IsGenericType)
        {
            var name = type.Name[..type.Name.IndexOf('`')];
            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
        }

        return type.Name switch
        {
            "Boolean" => "bool", "Byte" => "byte", "Double" => "double", "Int32" => "int",
            "Int64" => "long", "Single" => "float", "String" => "string", "Object" => "object",
            _ => type.Name,
        };
    }

    private static string MemberKey(MemberInfo member) => member switch
    {
        Type t => $"T:{t.FullName}",
        PropertyInfo p => $"P:{p.DeclaringType!.FullName}.{p.Name}",
        ConstructorInfo c => $"M:{c.DeclaringType!.FullName}.#ctor("
            + string.Join(",", c.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name))
            + ")",
        MethodInfo m => $"M:{m.DeclaringType!.FullName}.{m.Name}("
            + string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name))
            + ")",
        _ => member.Name,
    };
}

/// <summary>The compiler's XML output, read for summaries. Absent documentation degrades to a bare
/// signature rather than failing — a missing XML file must not break a build.</summary>
internal sealed class XmlDocumentation
{
    private readonly Dictionary<string, string> _summaries;

    private XmlDocumentation(Dictionary<string, string> summaries) => _summaries = summaries;

    /// <summary>Documentation for one or more assemblies, merged. More than one because the surface
    /// now describes types from <c>TradingTerminal.Core</c> as well as from the SDK, and a type
    /// documented in the wrong file would come out blank rather than wrong — which is harder to
    /// notice.</summary>
    public static XmlDocumentation LoadFor(params Assembly[] assemblies)
    {
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var assembly in assemblies)
        {
            var path = Path.ChangeExtension(assembly.Location, ".xml");
            if (!File.Exists(path)) continue;

            foreach (var member in XDocument.Load(path).Descendants("member"))
            {
                var name = member.Attribute("name")?.Value;
                var summary = member.Element("summary");
                if (name is null || summary is null) continue;

                var text = Flatten(summary);
                if (text.Length > 0) summaries[name] = text;
            }
        }

        return new XmlDocumentation(summaries);
    }

    public string? Summary(string key) => _summaries.GetValueOrDefault(key);

    /// <summary>Collapses the doc XML to one paragraph: <c>&lt;c&gt;</c> and <c>&lt;see&gt;</c> become
    /// backticks and whitespace is normalised.
    ///
    /// <para>Walks immediate children and recurses, rather than <c>DescendantNodes</c> — the latter
    /// yields the text inside an inline element a second time, so every <c>&lt;c&gt;</c> came out
    /// duplicated.</para></summary>
    private static string Flatten(XElement element)
    {
        var text = new StringBuilder();
        Walk(element, text);

        // Paragraph boundaries survive as a marker. The lead sentence of a summary says what a type IS;
        // the <para> blocks say why it is the way it is. Both are worth having, but only the first is
        // worth putting in a prompt prefix that is paid for on every session — so the boundary has to
        // still be there when the generator decides how much to emit.
        var lines = text.ToString().Split(ParagraphMark);
        for (var index = 0; index < lines.Length; index++)
            lines[index] = string.Join(' ', lines[index].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return string.Join(
            ParagraphMark,
            lines.Where(line => line.Length > 0));
    }

    /// <summary>
    /// Separates paragraphs inside a flattened summary.
    ///
    /// <para>A form feed, because it is the one character that cannot already be in the text. A newline
    /// was tried first and was wrong for a reason worth recording: the source XML wraps its own doc
    /// comments across lines, so every wrap became a paragraph break and sentences were cut mid-phrase —
    /// the surface came out telling authors to "push points inside the scope with", and then, on a fresh
    /// paragraph, "`Push`."</para>
    /// </summary>
    internal const char ParagraphMark = '\f';

    /// <summary>
    /// The readable tail of a documentation reference: <c>M:DaxAlgo.Sdk.IRenderSurface.Push(Double,Double)</c>
    /// becomes <c>Push</c>.
    ///
    /// <para>The parameter list must be removed <b>before</b> splitting on dots, or the last segment is
    /// whatever the final argument type happened to be — which is how the surface first came out
    /// telling authors to "push points with `Double)`".</para>
    /// </summary>
    private static string ShortName(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return string.Empty;

        var text = reference;
        if (text.Length > 1 && text[1] == ':') text = text[2..];      // strip the T:/M:/P:/F: prefix
        var parenthesis = text.IndexOf('(');
        if (parenthesis >= 0) text = text[..parenthesis];

        return text.Split('.')[^1];
    }

    private static void Walk(XElement element, StringBuilder text)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText t:
                    text.Append(t.Value);
                    break;

                case XElement { Name.LocalName: "c" or "see" or "paramref" or "seealso" } inline:
                    // A cref/name reference is usually an empty element, so the attribute carries the
                    // text.
                    var value = inline.Value.Length > 0
                        ? inline.Value
                        : ShortName((inline.Attribute("cref") ?? inline.Attribute("name"))?.Value);
                    if (value.Length > 0) text.Append('`').Append(value).Append('`');
                    break;

                case XElement { Name.LocalName: "para" } paragraph:
                    text.Append(ParagraphMark);
                    Walk(paragraph, text);
                    text.Append(ParagraphMark);
                    break;

                case XElement nested:
                    text.Append(' ');
                    Walk(nested, text);
                    break;
            }
        }
    }
}
