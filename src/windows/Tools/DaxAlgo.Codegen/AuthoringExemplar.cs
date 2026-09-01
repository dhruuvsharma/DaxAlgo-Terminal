using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// A complete, compiling unit of each kind, shown to the model as the shape its answer should take.
///
/// <para><b>Why an exemplar and not more prose.</b> Everything else the model is given describes the
/// contracts — the generated surface lists signatures, the skill packs explain widgets and layout, the
/// kind block states the rules. None of that shows what a finished unit <i>looks like</i>: where the
/// schema goes, how state is kept between a data callback and <c>Draw</c>, what a real parameter read
/// looks like. Issue #44 calls this the strongest teaching signal available, and it was the last of
/// its eight phases with nothing wired to it.</para>
///
/// <para><b>These are not written for the prompt.</b> They are
/// <c>samples/DaxAlgo.Sandbox.Samples</c> — a real project, compiled by CI, covered by tests that
/// drive the kernel and host the visualizer. That is what makes them <i>verified</i> exemplars rather
/// than a snippet someone believed was right: a sample that stops compiling breaks the build, and a
/// sample whose behaviour drifts breaks its tests. Prose in a prompt has neither.</para>
///
/// <para><b>They are normalised before the model sees them.</b> The sample files are library code with
/// <c>using</c> directives and a namespace; an authored unit must have neither. Shipping them raw
/// would teach the model to write exactly what the rules two paragraphs above forbid, which is worse
/// than showing no example at all — a contradiction the model resolves by guessing.</para>
/// </summary>
public static class AuthoringExemplar
{
    private const string ResourcePrefix = "DaxAlgo.Codegen.Exemplars.";

    /// <summary>File-scoped namespace, or the opening line of a block-scoped one.</summary>
    private static readonly Regex NamespaceLine = new(
        @"^\s*namespace\s+[^\s;{]+\s*[;{]\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex UsingLine = new(
        @"^\s*using\s+[^;]+;\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Words that make an order-flow exemplar the right one to show.
    ///
    /// <para>Deliberately the order-flow skill's own triggers, narrowed to the ones that imply DEPTH or
    /// THE TAPE rather than order flow as a topic. A brief that merely says "delta" is not asking for a
    /// book.</para>
    /// </summary>
    private static readonly string[] OrderFlowWords =
    [
        "order flow", "orderflow", "order book", "orderbook", "book", "depth", "dom", "ladder",
        "footprint", "tape", "imbalance", "microprice", "vpin", "toxicity", "liquidity", "sweep",
        "absorption", "iceberg", "queue", "bid ask", "microstructure",
    ];

    /// <summary>
    /// Words that make the THREE-DIMENSIONAL exemplar the right one to show, and it outranks the
    /// order-flow one.
    ///
    /// <para>A brief asking for a book in space is asking for both, and the landscape teaches both:
    /// it consumes depth snapshots exactly as the order-flow exemplar does, and it is the only worked
    /// example of projection and of composing a picture out of primitives rather than calling a
    /// widget. Depth handling shown twice costs nothing; projection shown nowhere is the gap.</para>
    ///
    /// <para>Kept narrow on purpose. Exactly one exemplar is ever sent, so a list that also caught
    /// "surface" or "cube" would quietly take the order-flow example away from ordinary book
    /// briefs — the expensive direction, and pinned as such in
    /// <c>ThreeDimensionalUnitTests</c>.</para>
    /// </summary>
    /// <summary>
    /// Words for a picture built CELL BY CELL — a grid of values with a shared axis, which no widget
    /// in the library draws and which is the shape most of the hand-written windows actually are.
    ///
    /// <para>It outranks the order-flow list, and deliberately overlaps it: "footprint" is in both.
    /// A footprint brief answered with the order-flow exemplar gets a worked example that delegates
    /// every picture to a widget call, and produces one — which is the whole reason generated windows
    /// came out looking like the widget library rather than like a trading screen.</para>
    /// </summary>
    private static readonly string[] CellGridWords =
    [
        "footprint", "cluster", "cell", "grid", "matrix", "heatmap", "heat map", "profile",
        "volume at price", "histogram by price", "regime graph", "per price", "by price",
    ];

    /// <summary>Words that mean the unit needs DEPTH, which the composed-scene exemplar does not
    /// consume — so they send the brief to the order-flow one however cell-shaped it also is.</summary>
    private static readonly string[] BookWords =
    [
        "order book", "orderbook", "book", "depth", "dom", "ladder", "microprice", "sweep", "queue",
        "bid ask", "resting", "absorption", "iceberg",
    ];

    private static readonly string[] SpatialWords =
    [
        "3d", "3-d", "three dimensional", "three-dimensional", "battlefield", "isometric", "perspective",
    ];

    /// <summary>
    /// The exemplar for one kind and brief, already normalised, or empty when none is embedded.
    /// </summary>
    /// <param name="kind">What is being authored.</param>
    /// <param name="brief">
    /// The user's own words, when they are known.
    ///
    /// <para><b>Skills are matched to the brief and the exemplar was not</b>, which left the one
    /// combination people actually ask for mismatched: "show me a footprint chart" loaded the
    /// order-flow skill and a spread-band exemplar that never touches depth or the tape. The strongest
    /// teaching signal in the pack was the one piece not aimed at the question.</para>
    ///
    /// <para>Null or empty keeps the default, which is what a resumed session with no user text gets.</para>
    /// </param>
    public static string For(AuthoringKind kind, string? brief = null)
    {
        // Matched explicitly rather than with a ternary. A ternary makes every unrecognised kind the
        // strategy exemplar, so a kind added later would silently be taught the wrong contract — the
        // one failure here that nothing downstream could catch, because a plausible exemplar for the
        // wrong kind reads exactly like a correct one.
        var file = Source(kind, brief);
        if (file is null) return string.Empty;

        var name = ResourcePrefix + file;
        var assembly = typeof(AuthoringExemplar).Assembly;
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return string.Empty;

        using var reader = new StreamReader(stream);
        return Normalise(reader.ReadToEnd());
    }

    /// <summary>
    /// The exemplar wrapped as the file block the model is asked to produce, ready to append to a kind
    /// brief. Empty when there is no exemplar, so a missing resource degrades to the prompt as it was
    /// rather than to a heading with nothing under it.
    /// </summary>
    public static string Block(AuthoringKind kind, string? brief = null)
    {
        var source = For(kind, brief);
        var file = Source(kind, brief);
        if (string.IsNullOrWhiteSpace(source) || file is null) return string.Empty;

        return new StringBuilder()
            .AppendLine("### A complete unit of this kind")
            .AppendLine()
            .AppendLine(
                "This compiles, runs, and is covered by tests in this repository. It is the shape and "
                + "the level of detail to aim for — not a template to fill in, and not a strategy to "
                + "reproduce unless the user asked for this one.")
            .AppendLine()
            .AppendLine($"```csharp")
            .AppendLine($"// file: {file}")
            .AppendLine(source.TrimEnd())
            .AppendLine("```")
            .ToString();
    }

    /// <summary>
    /// The sample file backing a kind and brief, or null when that kind has no exemplar.
    ///
    /// <para>Only the visualizer side has a second exemplar so far, because the book and the tape are
    /// what a generated unit has no other worked example of. An order-flow STRATEGY still gets the
    /// cross, which teaches the kernel shape correctly and simply says less about depth.</para>
    /// </summary>
    private static string? Source(AuthoringKind kind, string? brief) => kind switch
    {
        // A strategy brief shaped like a SCREEN gets the composed one. Everything else keeps the
        // cross, which teaches the kernel contract and a price chart correctly and simply.
        AuthoringKind.Strategy => WantsCells(brief)
            ? "RegimeMatrixKernel.cs"
            : "MovingAverageCrossKernel.cs",
        AuthoringKind.Visualizer => WantsSpace(brief)
            ? "DepthLandscapeVisualizer.cs"
            : WantsCells(brief)
                ? "FootprintClusterVisualizer.cs"
                : WantsOrderFlow(brief)
                    ? "BookPressureVisualizer.cs"
                    : "SpreadBandVisualizer.cs",
        _ => null,
    };

    /// <summary>True when the brief asks for a picture in three dimensions.</summary>
    internal static bool WantsSpace(string? brief) => Mentions(brief, SpatialWords);

    /// <summary>
    /// True when the brief asks for a grid of values against a shared axis — the shape that has to be
    /// composed from primitives because no widget draws it.
    ///
    /// <para><b>And not when it also asks for the book.</b> The exemplar has to cover the DATA the
    /// brief needs before it covers the shape: "a footprint chart with the order book beside it" needs
    /// depth, and the composed-scene sample consumes only bars and the tape, so answering it here
    /// would teach a better picture drawn from a stream the unit never subscribed to. Among exemplars
    /// that carry the right data, prefer the one that composes.</para>
    /// </summary>
    internal static bool WantsCells(string? brief) =>
        Mentions(brief, CellGridWords) && !Mentions(brief, BookWords);

    /// <summary>True when the brief is about the book or the tape rather than a price series.</summary>
    internal static bool WantsOrderFlow(string? brief) => Mentions(brief, OrderFlowWords);

    private static bool Mentions(string? brief, string[] words)
    {
        if (string.IsNullOrWhiteSpace(brief)) return false;

        foreach (var word in words)
        {
            if (brief.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Turns library source into the shape an authored unit takes: no <c>using</c> directives, no
    /// namespace, and the class back at column zero.
    ///
    /// <para>De-indenting matters as much as the rest. A block-scoped namespace leaves every line four
    /// spaces in, and an exemplar indented differently from the answer it is teaching invites the model
    /// to reproduce the indentation and nothing else about the shape.</para>
    /// </summary>
    internal static string Normalise(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        var text = UsingLine.Replace(source, string.Empty);
        var blockScoped = NamespaceLine.IsMatch(text) && text.Contains('{')
            && NamespaceLine.Match(text).Value.TrimEnd().EndsWith('{');

        text = NamespaceLine.Replace(text, string.Empty);

        if (blockScoped)
        {
            // Drop the namespace's closing brace — the last one in the file — and de-indent.
            var lastBrace = text.LastIndexOf('}');
            if (lastBrace >= 0) text = text.Remove(lastBrace, 1);
            text = Dedent(text);
        }

        return text.Trim('\r', '\n', ' ', '\t') + "\n";
    }

    /// <summary>Removes the common leading indentation from every non-blank line.</summary>
    private static string Dedent(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var common = int.MaxValue;

        foreach (var line in lines)
        {
            if (line.Trim().Length == 0) continue;
            common = Math.Min(common, line.Length - line.TrimStart(' ').Length);
            if (common == 0) return text;
        }

        if (common is 0 or int.MaxValue) return text;

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length >= common) lines[i] = lines[i][common..];
        }

        return string.Join('\n', lines);
    }
}
