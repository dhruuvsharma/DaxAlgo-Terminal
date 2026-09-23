using System.Text;
using System.Text.RegularExpressions;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;

/// <summary>One change to one file: the lines to find, and what replaces them.</summary>
/// <param name="File">The file the edit names, or null when the reply named none.</param>
/// <param name="Search">Lines copied from the current file. Empty means "append".</param>
/// <param name="Replace">The lines that take their place.</param>
/// <param name="Malformed">The block had a divider where no divider belongs, so its halves cannot be told
/// apart; it is never applied.</param>
public sealed record FileEdit(string? File, string Search, string Replace, bool Malformed = false);

/// <summary>What applying a reply's edits produced.</summary>
/// <param name="Edited">Every file whose edits all applied, with its new content.</param>
/// <param name="Failed">Files with an edit that matched nothing — left exactly as they were.</param>
/// <param name="Unmatched">The first line of each edit that matched nothing, for the follow-up.</param>
public sealed record EditOutcome(
    IReadOnlyList<StrategyFile> Edited,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> Unmatched);

/// <summary>
/// Repairs as edits rather than whole files.
///
/// <para><b>A repair's output used to be the size of the file, not the size of the fix.</b> Every fixer
/// was told to return its file complete, so a two-line fault in a 72,000-character page module cost a
/// 22,000-token reply, and a unit class re-sent all 30,000 characters for each of four compile errors.
/// Measured on the 2026-09-20 Nemotron Battlefield run: repairs were most of its 185,097 output tokens.
/// An edit is the lines that change and enough around them to find the place.</para>
///
/// <para><b>The format is the search/replace block</b> that coding models are trained on:</para>
/// <code>
/// ```edit
/// // file: ui/scene.js
/// &lt;&lt;&lt;&lt;&lt;&lt;&lt; SEARCH
/// camera.position.set(0, 10, 20);
/// =======
/// camera.position.set(0, 60, 170);
/// &gt;&gt;&gt;&gt;&gt;&gt;&gt; REPLACE
/// ```
/// </code>
///
/// <para><b>All or nothing per file.</b> A file whose edits do not all match is left untouched and
/// reported, never half-applied: half a fix can compile into something worse than the fault it was
/// fixing, and the caller asks once for that file whole instead.</para>
/// </summary>
public static partial class EditBlocks
{
    /// <summary>Opens the lines to find.</summary>
    public const string SearchMarker = "<<<<<<< SEARCH";

    /// <summary>Separates the lines to find from their replacement.</summary>
    public const string DividerMarker = "=======";

    /// <summary>Closes the replacement.</summary>
    public const string ReplaceMarker = ">>>>>>> REPLACE";

    [GeneratedRegex(@"^\s*(?:<!--|//|/\*|#)?\s*file\s*:\s*(?<name>[\w.\-/\\]+\.(?:cs|html?|m?js|css|svg))\s*(?:-->|\*/)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FileLine();

    [GeneratedRegex(@"(?<name>[\w.\-/\\]+\.(?:cs|html?|m?js|css|svg))\b", RegexOptions.IgnoreCase)]
    private static partial Regex FileMention();

    [GeneratedRegex(@"^\s*<{5,9}\s*SEARCH\s*$")]
    private static partial Regex SearchLine();

    [GeneratedRegex(@"^\s*={5,9}\s*$")]
    private static partial Regex DividerLine();

    [GeneratedRegex(@"^\s*>{5,9}\s*REPLACE\s*$")]
    private static partial Regex ReplaceLine();

    /// <summary>True when a reply — or a block in it — carries search/replace edits.</summary>
    public static bool Present(string? text) =>
        !string.IsNullOrEmpty(text) && text.Split('\n').Any(l => SearchLine().IsMatch(l.TrimEnd('\r')));

    /// <summary>Every edit in a reply, in order, each with the file named most recently above it.</summary>
    public static IReadOnlyList<FileEdit> Parse(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return [];

        var edits = new List<FileEdit>();
        string? file = null;
        var search = new StringBuilder();
        var replace = new StringBuilder();
        var state = 0; // 0 outside, 1 in SEARCH, 2 in REPLACE
        var pendingDivider = false;
        var malformed = false;

        foreach (var raw in reply.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw;

            if (state == 0)
            {
                if (SearchLine().IsMatch(line))
                {
                    state = 1;
                    search.Clear();
                    replace.Clear();
                    pendingDivider = false;
                    malformed = false;
                    continue;
                }

                // A file named by a header line, or on a fence's info string ("```edit ui/scene.js").
                var trimmed = line.Trim();
                if (FileLine().Match(line) is { Success: true } header)
                    file = Normalise(header.Groups["name"].Value);
                else if (trimmed.StartsWith("```", StringComparison.Ordinal) && FileMention().Match(trimmed) is { Success: true } fence)
                    file = Normalise(fence.Groups["name"].Value);

                continue;
            }

            if (state == 1)
            {
                if (DividerLine().IsMatch(line))
                {
                    state = 2;
                    continue;
                }

                Append(search, line);
                continue;
            }

            // A SECOND DIVIDER. Measured on NIM's Kimi K3, 2026-09-24: a fixer closed an edit with
            // "=======" and then ">>>>>>> REPLACE", the stray divider was copied into the replacement, and
            // the file compiled to CS8300 "merge conflict marker" for three rounds — because every later
            // edit trying to remove that line was itself cut in two by it. One directly before REPLACE is
            // that habit and is dropped; one anywhere else makes the block ambiguous, and it is marked so
            // the file is asked for whole instead.
            if (DividerLine().IsMatch(line))
            {
                if (pendingDivider) malformed = true;
                pendingDivider = true;
                continue;
            }

            if (ReplaceLine().IsMatch(line))
            {
                edits.Add(new FileEdit(file, search.ToString(), replace.ToString(), malformed));
                state = 0;
                pendingDivider = false;
                malformed = false;
                continue;
            }

            if (pendingDivider)
            {
                malformed = true;
                pendingDivider = false;
                Append(replace, DividerMarker);
            }

            Append(replace, line);
        }

        return edits;
    }

    /// <summary>A line that is one of this format's markers — never valid in any file an edit targets.</summary>
    [GeneratedRegex(@"^\s*(?:<{7}\s*SEARCH|={7}|>{7}\s*REPLACE)\s*$", RegexOptions.Multiline)]
    private static partial Regex MarkerLine();

    /// <summary>
    /// Applies edits to the files they name.
    /// </summary>
    /// <param name="edits">What the reply asked for.</param>
    /// <param name="files">The files the edits may touch — the task's own.</param>
    /// <param name="defaultFile">Where an edit that names no file goes: the task's one file, when it has
    /// exactly one. Null drops such edits as unmatched.</param>
    public static EditOutcome Apply(IReadOnlyList<FileEdit> edits, IReadOnlyList<StrategyFile> files, string? defaultFile)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(files);

        var edited = new List<StrategyFile>();
        var failed = new List<string>();
        var unmatched = new List<string>();

        foreach (var group in edits.GroupBy(e => Resolve(e.File ?? defaultFile, files)?.Name ?? e.File ?? "(no file)", StringComparer.OrdinalIgnoreCase))
        {
            var target = files.FirstOrDefault(f => string.Equals(f.Name, group.Key, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                failed.Add(group.Key);
                unmatched.AddRange(group.Select(e => $"{group.Key}: {FirstLine(e.Search)} (not one of your files)"));
                continue;
            }

            var content = target.Content.Replace("\r\n", "\n");
            var ok = true;
            foreach (var edit in group)
            {
                if (edit.Malformed)
                {
                    ok = false;
                    unmatched.Add($"{target.Name}: {FirstLine(edit.Search)} (the block had a second ======= line, so where its replacement starts is ambiguous)");
                    continue;
                }

                if (TryApply(content, edit) is { } next)
                {
                    content = next;
                    continue;
                }

                ok = false;
                unmatched.Add($"{target.Name}: {FirstLine(edit.Search)}");
            }

            // Never hand back a file carrying this format's own markers, whatever the edits said: it does not
            // compile, and no later edit can reach the line to take it out again.
            if (ok && MarkerLine().IsMatch(content) && !MarkerLine().IsMatch(target.Content))
            {
                ok = false;
                unmatched.Add($"{target.Name}: the edits would leave a <<<<<<< / ======= / >>>>>>> line in the file");
            }

            if (ok) edited.Add(new StrategyFile(target.Name, content));
            else failed.Add(target.Name);
        }

        return new EditOutcome(edited, failed, unmatched);
    }

    /// <summary>
    /// One edit against one file's text, or null when its lines are not there.
    ///
    /// <para>Exact first. Then line by line ignoring trailing whitespace, then ignoring indentation too —
    /// models re-indent what they copy, and a match on the same lines in the same order is the same
    /// place. The first occurrence wins, as it does for every tool that reads this format.</para>
    /// </summary>
    internal static string? TryApply(string content, FileEdit edit)
    {
        var search = edit.Search.Replace("\r\n", "\n");
        var replace = edit.Replace.Replace("\r\n", "\n");

        if (search.Trim().Length == 0)
            return content.EndsWith('\n') || content.Length == 0 ? content + replace + "\n" : content + "\n" + replace + "\n";

        // Exact, and on whole lines: a search that starts mid-line is a copy that lost its indentation,
        // and splicing the replacement in there would indent it twice. The line matches below handle it.
        for (var exact = content.IndexOf(search, StringComparison.Ordinal);
             exact >= 0;
             exact = content.IndexOf(search, exact + 1, StringComparison.Ordinal))
        {
            var end = exact + search.Length;
            var startsLine = exact == 0 || content[exact - 1] == '\n';
            var endsLine = end == content.Length || content[end] == '\n';
            if (startsLine && endsLine) return content[..exact] + replace + content[end..];
        }

        var lines = content.Split('\n');
        var wanted = search.Split('\n');

        // Blank lines at the edges of a search block are how models space it, not part of the match.
        var first = 0;
        var last = wanted.Length - 1;
        while (first <= last && wanted[first].Trim().Length == 0) first++;
        while (last >= first && wanted[last].Trim().Length == 0) last--;
        if (first > last) return null;
        wanted = wanted[first..(last + 1)];

        foreach (var compare in new Func<string, string>[] { s => s.TrimEnd(), s => s.Trim() })
        {
            for (var at = 0; at + wanted.Length <= lines.Length; at++)
            {
                var match = true;
                for (var k = 0; k < wanted.Length && match; k++)
                    match = string.Equals(compare(lines[at + k]), compare(wanted[k]), StringComparison.Ordinal);

                if (!match) continue;

                var middle = replace.TrimEnd('\n');
                IEnumerable<string> result = lines[..at];
                if (middle.Length > 0) result = result.Concat(middle.Split('\n'));
                return string.Join('\n', result.Concat(lines[(at + wanted.Length)..]));
            }
        }

        // Last, part of a line — a model changing one token copies only that stretch of it.
        var within = content.IndexOf(search, StringComparison.Ordinal);
        return within >= 0 ? content[..within] + replace + content[(within + search.Length)..] : null;
    }

    /// <summary>The follow-up a fixer gets when some of its edits matched nothing.</summary>
    public static string RetryPrompt(IReadOnlyList<string> files, IReadOnlyList<string> unmatched) =>
        "These edits did not match the current file, so " + (files.Count == 1 ? "that file was" : "those files were")
        + " left exactly as they were:" + Environment.NewLine
        + string.Join(Environment.NewLine, unmatched.Take(8).Select(u => "- " + u)) + Environment.NewLine + Environment.NewLine
        + "Return " + string.Join(", ", files) + " COMPLETE instead — each in one fenced block with its `file:` "
        + "header on the first line, with your fix applied. No edit blocks this time.";

    private static StrategyFile? Resolve(string? name, IReadOnlyList<StrategyFile> files)
    {
        if (string.IsNullOrWhiteSpace(name)) return files.Count == 1 ? files[0] : null;

        var normal = Normalise(name);
        return files.FirstOrDefault(f => string.Equals(f.Name, normal, StringComparison.OrdinalIgnoreCase))
               ?? files.FirstOrDefault(f => string.Equals(Leaf(f.Name), Leaf(normal), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A page file keeps its folder; a bare page name is put in it. C# is a leaf.</summary>
    private static string Normalise(string name)
    {
        var path = name.Trim().Replace('\\', '/').TrimStart('/');
        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return Leaf(path);
        return CodegenCodeExtractor.IsPageFile(path) ? path : CodegenCodeExtractor.PageFolder + Leaf(path);
    }

    private static string Leaf(string path) => path[(path.Replace('\\', '/').LastIndexOf('/') + 1)..];

    private static string FirstLine(string search)
    {
        var line = search.Replace("\r\n", "\n").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(empty)";
        return line.Length > 120 ? line[..120] + " …" : line;
    }

    private static void Append(StringBuilder text, string line)
    {
        if (text.Length > 0) text.Append('\n');
        text.Append(line);
    }
}
