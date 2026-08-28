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

    /// <summary>The exemplar for one kind, already normalised, or empty when none is embedded.</summary>
    public static string For(AuthoringKind kind)
    {
        // Matched explicitly rather than with a ternary. A ternary makes every unrecognised kind the
        // strategy exemplar, so a kind added later would silently be taught the wrong contract — the
        // one failure here that nothing downstream could catch, because a plausible exemplar for the
        // wrong kind reads exactly like a correct one.
        var file = Source(kind);
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
    public static string Block(AuthoringKind kind)
    {
        var source = For(kind);
        var file = Source(kind);
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

    /// <summary>The sample file backing a kind, or null when that kind has no exemplar.</summary>
    private static string? Source(AuthoringKind kind) => kind switch
    {
        AuthoringKind.Strategy => "MovingAverageCrossKernel.cs",
        AuthoringKind.Visualizer => "SpreadBandVisualizer.cs",
        _ => null,
    };

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
