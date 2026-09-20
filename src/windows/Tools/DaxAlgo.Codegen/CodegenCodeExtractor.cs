using System.Text.RegularExpressions;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Pulls the C# out of a model reply. Models wrap code in <c>```csharp … ```</c> fences, usually with
/// prose around it; a multi-file answer is several fences, each labelled with a file name. Labels are
/// read from (in order) the fence info string (<c>```csharp MyStrategy.cs</c>), a <c>// file: X.cs</c>
/// first line inside the block, or a file name mentioned on the line just above the fence. Unlabelled
/// blocks get positional names, so a plain single-block reply still works.
/// <para>Non-C# fences (json, powershell, …) are skipped: a model that explains its plugin.json must not
/// have it compiled as C#.</para>
/// </summary>
public static partial class CodegenCodeExtractor
{
    [GeneratedRegex(@"```(?<lang>[a-zA-Z#+]*)[ \t]*(?<info>[^\n]*)\n(?<body>.*?)```", RegexOptions.Singleline)]
    private static partial Regex FencedBlock();

    /// <summary>A <c>// file: Name.cs</c> (or <c>// Name.cs</c>) marker on the block's first line.</summary>
    [GeneratedRegex(@"^[ \t]*//[ \t]*(?:file[ \t]*:[ \t]*)?(?<name>[\w.\-]+\.cs)[ \t]*\r?\n", RegexOptions.IgnoreCase)]
    private static partial Regex FileHeader();

    /// <summary>A bare file name mentioned in prose/info strings — <c>MyStrategy.cs</c>, `**Kernel.cs**`.</summary>
    [GeneratedRegex(@"(?<name>[\w.\-]+\.cs)")]
    private static partial Regex FileNameMention();

    /// <summary>A page file's own path marker on the block's first line, in whichever comment syntax
    /// its language has: <c>&lt;!-- file: ui/index.html --&gt;</c>, <c>// file: ui/app.js</c>,
    /// <c>/* file: ui/style.css */</c>.</summary>
    [GeneratedRegex(@"^[ \t]*(?:<!--|//|/\*)[ \t]*(?:file[ \t]*:[ \t]*)?(?<name>[\w.\-/]+\.(?:html?|m?js|css|svg))[ \t]*(?:-->|\*/)?[ \t]*\r?\n", RegexOptions.IgnoreCase)]
    private static partial Regex PageHeader();

    /// <summary>
    /// A <c>file:</c> header on its own line, in any of the comment syntaxes, for a reply that wrote its
    /// files with headers and NO fences.
    ///
    /// <para>The word "file" is required here, unlike inside a fence: without a fence to say where code
    /// begins, a bare name in a sentence is prose.</para>
    /// </summary>
    [GeneratedRegex(@"^[ \t]*(?:<!--|//|/\*)[ \t]*file[ \t]*:[ \t]*(?<name>[\w.\-/]+\.(?:cs|html?|m?js|css|svg))[ \t]*(?:-->|\*/)?[ \t]*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex HeaderedFile();

    /// <summary>A page file named in an info string or the prose line above a fence.</summary>
    [GeneratedRegex(@"(?<name>(?:ui/)?[\w.\-/]*[\w\-]\.(?:html?|m?js|css|svg))\b", RegexOptions.IgnoreCase)]
    private static partial Regex PageNameMention();

    private static readonly string[] CSharpLanguages = ["csharp", "cs", "c#", ""];

    /// <summary>Fence languages that are a unit's web page, and the file a block of each is called when
    /// it names none.</summary>
    private static readonly Dictionary<string, string> PageLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["html"] = "ui/index.html",
        ["htm"] = "ui/index.html",
        ["js"] = "ui/app.js",
        ["javascript"] = "ui/app.js",
        ["mjs"] = "ui/app.js",
        ["css"] = "ui/style.css",
        ["svg"] = "ui/image.svg",
    };

    /// <summary>The folder a unit's page lives in.</summary>
    public const string PageFolder = "ui/";

    /// <summary>True for a file that belongs to a unit's web page rather than its C#.</summary>
    public static bool IsPageFile(string? name) =>
        name is not null && name.Replace('\\', '/').StartsWith(PageFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first C# block (or the whole reply when unfenced) — the single-file path.</summary>
    public static string Extract(string? reply)
    {
        var files = ExtractFiles(reply);
        return files.Count > 0 ? files[0].Content : string.Empty;
    }

    /// <summary>
    /// The model's words with its code taken out, each block replaced by a one-line note of what it wrote.
    /// This is what keeps a long conversation affordable: a rewritten file is superseded the moment the
    /// next version arrives, but the naive thread keeps every copy forever and re-sends all of them on
    /// every turn. The prose is what a follow-up actually depends on; the code lives in the editor, and
    /// the session ships one current copy of it.
    /// </summary>
    public static string StripCode(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return string.Empty;

        return FencedBlock().Replace(reply, match =>
        {
            var language = match.Groups["lang"].Value.Trim().ToLowerInvariant();
            var page = PageLanguages.ContainsKey(language);

            // Json and shell stay: they are tiny. A page does not — it is as superseded by the next
            // version as the C# is, and usually longer.
            if (!page && !CSharpLanguages.Contains(language)) return match.Value;

            var body = match.Groups["body"].Value;
            var header = page ? PageHeader().Match(body) : FileHeader().Match(body);
            var name = header.Success ? header.Groups["name"].Value : "a file";
            var lines = body.Count(c => c == '\n');

            return $"[code omitted: wrote {name} ({lines} lines) — the current version is below]";
        }).Trim();
    }

    /// <summary>
    /// Every fenced block's body, whatever language it claims — in the order the model wrote them.
    ///
    /// <para>Fence parsing lives here rather than being re-implemented by each caller, because a fence
    /// is harder than it looks (an info string, an optional language, a body that may itself contain
    /// backticks) and a second implementation would disagree with this one on exactly the replies worth
    /// getting right. The planner reads JSON out of a fence through this.</para>
    /// </summary>
    public static IReadOnlyList<string> FencedBlocks(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return [];

        return [.. FencedBlock().Matches(reply).Select(m => m.Groups["body"].Value)];
    }

    /// <summary>
    /// Every C# file in the reply, in order. Empty when the model wrote no code — which is a legitimate
    /// turn (it asked a question), not a failure.
    /// </summary>
    public static IReadOnlyList<StrategyFile> ExtractFiles(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return [];

        var matches = FencedBlock().Matches(reply);
        if (matches.Count == 0)
        {
            // Headers without fences: the files are all there and named, so they are taken — and a reply
            // that named ONLY page files has written no C# at all. Without that second half, a page module
            // written as plain JavaScript reads as "looks like code" and arrives as one .cs file that no
            // page task can accept (measured on Nemotron 3 Ultra's ui/scene.js, 22,301 tokens).
            if (HeaderedFiles(reply) is { Count: > 0 } headered)
                return [.. headered.Where(f => !IsPageFile(f.Name))];

            // Unfenced. Only treat it as code if it looks like code — otherwise it's prose (a question),
            // and compiling prose would bury the model's actual answer under 40 syntax errors.
            var bare = reply.Trim();
            return LooksLikeCSharp(bare) ? [new StrategyFile(StrategyFile.DefaultName, bare)] : [];
        }

        var files = new List<StrategyFile>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in matches)
        {
            var language = match.Groups["lang"].Value.Trim().ToLowerInvariant();
            if (!CSharpLanguages.Contains(language)) continue;

            var body = match.Groups["body"].Value;
            var name = NameFor(match, reply, body, out var strippedBody);
            body = strippedBody.Trim();
            if (body.Length == 0) continue;

            files.Add(new StrategyFile(Unique(name ?? PositionalName(files.Count), used), body));
        }

        return files;
    }

    /// <summary>
    /// Every file of a Blocks unit in the reply: its C# exactly as <see cref="ExtractFiles"/> reads it,
    /// and its web page — <c>html</c>, <c>js</c> and <c>css</c> fences — under <c>ui/</c>.
    ///
    /// <para>Separate from <see cref="ExtractFiles"/> because the widget SDK's compiler takes every file
    /// it is handed as C#: a page slipping into that path would arrive as forty syntax errors in a file
    /// nobody meant as a program.</para>
    /// </summary>
    public static IReadOnlyList<StrategyFile> ExtractUnitFiles(string? reply)
    {
        var files = ExtractFiles(reply).ToList();
        if (string.IsNullOrWhiteSpace(reply)) return files;

        var used = new HashSet<string>(files.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        foreach (Match match in FencedBlock().Matches(reply))
        {
            var language = match.Groups["lang"].Value.Trim();
            if (!PageLanguages.TryGetValue(language, out var fallback)) continue;

            var body = match.Groups["body"].Value;
            var name = PageNameFor(match, reply, body, out var stripped);
            body = stripped.Trim();
            if (body.Length == 0) continue;

            files.Add(new StrategyFile(UniquePage(name ?? fallback, used), body));
        }

        // NOTHING FENCED, BUT EVERY FILE NAMED. Measured 2026-09-20 on NVIDIA NIM's Nemotron 3 Ultra: its
        // page replies opened straight at "<!-- file: ui/index.html -->" and ran to a complete page and a
        // complete module, with no fence anywhere — 24,000 tokens of finished work read as "returned no
        // file", twice. A header on its own line is the model saying where a file starts as plainly as a
        // fence does.
        return files.Count > 0 ? files : HeaderedFiles(reply);
    }

    /// <summary>The files of a reply that named each one with a <c>file:</c> header and fenced none.</summary>
    internal static IReadOnlyList<StrategyFile> HeaderedFiles(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return [];

        var headers = HeaderedFile().Matches(reply);
        if (headers.Count == 0) return [];

        var files = new List<StrategyFile>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var at = 0; at < headers.Count; at++)
        {
            var start = headers[at].Index + headers[at].Length;
            var end = at + 1 < headers.Count ? headers[at + 1].Index : reply.Length;
            if (end <= start) continue;

            // A stray fence around the lot, and any closing fence, are not part of the file.
            var body = reply[start..end].Trim().Trim('`').Trim();
            if (body.Length == 0) continue;

            var written = headers[at].Groups["name"].Value.Trim();
            var name = written.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                ? Unique(written[(written.LastIndexOf('/') + 1)..], used)
                : UniquePage(InPageFolder(written), used);

            files.Add(new StrategyFile(name, body));
        }

        return files;
    }

    /// <summary>A page block's name: its header line, its info string, or the line above the fence —
    /// always under <c>ui/</c>, whether or not the model wrote the folder.</summary>
    private static string? PageNameFor(Match match, string reply, string body, out string strippedBody)
    {
        strippedBody = body;

        var header = PageHeader().Match(body);
        if (header.Success)
        {
            strippedBody = body[header.Length..];
            return InPageFolder(header.Groups["name"].Value);
        }

        var info = PageNameMention().Match(match.Groups["info"].Value);
        if (info.Success) return InPageFolder(info.Groups["name"].Value);

        var lineStart = reply.LastIndexOf('\n', Math.Max(0, match.Index - 2));
        if (lineStart >= 0 && match.Index - lineStart < 200)
        {
            var mention = PageNameMention().Match(reply[lineStart..match.Index]);
            if (mention.Success) return InPageFolder(mention.Groups["name"].Value);
        }

        return null;
    }

    private static string InPageFolder(string name)
    {
        var path = name.Replace('\\', '/').TrimStart('/');
        return IsPageFile(path) ? PageFolder + path[PageFolder.Length..] : PageFolder + path;
    }

    private static string UniquePage(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;

        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}{i}{extension}";
            if (used.Add(candidate)) return candidate;
        }
    }

    /// <summary>The file name for a block: its own header line (stripped from the body so the compiler's
    /// line numbers match what the user sees), the fence's info string, or the prose line above it.</summary>
    private static string? NameFor(Match match, string reply, string body, out string strippedBody)
    {
        strippedBody = body;

        var header = FileHeader().Match(body);
        if (header.Success)
        {
            strippedBody = body[header.Length..];
            return header.Groups["name"].Value;
        }

        var info = FileNameMention().Match(match.Groups["info"].Value);
        if (info.Success) return info.Groups["name"].Value;

        // The line immediately before the fence, e.g. "**MyStrategy.cs**" or "### Kernel.cs".
        var lineStart = reply.LastIndexOf('\n', Math.Max(0, match.Index - 2));
        if (lineStart >= 0 && match.Index - lineStart < 200)
        {
            var preceding = reply[lineStart..match.Index];
            var mention = FileNameMention().Match(preceding);
            if (mention.Success) return mention.Groups["name"].Value;
        }

        return null;
    }

    private static string PositionalName(int index) =>
        index == 0 ? StrategyFile.DefaultName : $"Strategy{index + 1}.cs";

    /// <summary>Two blocks claiming the same name would silently overwrite each other in the editor —
    /// suffix instead, and let the compiler's duplicate-type error (if any) speak for itself.</summary>
    private static string Unique(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;

        var stem = name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}{i}.cs";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static bool LooksLikeCSharp(string text) =>
        text.Contains("class ", StringComparison.Ordinal) ||
        text.Contains("struct ", StringComparison.Ordinal) ||
        text.Contains("record ", StringComparison.Ordinal) ||
        text.Contains("namespace ", StringComparison.Ordinal);

    /// <summary>
    /// Whether an extracted file plausibly contains C# at all.
    ///
    /// <para><b>Found by running it.</b> A model answered a fix prompt with PROSE wrapped in a
    /// <c>// file:</c> fence — a diagnosis of the previous failure rather than a corrected file. The
    /// extractor did exactly what the contract says and handed the prose to the compiler, which
    /// reported CS1003, CS1002 and "unexpected character '`'" from line 1. The fix loop then fed those
    /// back, so the next turn tried to FIX THE PROSE, and the turn after that wrote a paragraph
    /// explaining that the file contained no program. Three generations, all spent on text nobody
    /// meant as code.</para>
    ///
    /// <para>Deliberately a shape test rather than a parse: every real unit's first meaningful line is
    /// a using, a namespace, an attribute, a preprocessor directive or a type declaration, and prose is
    /// none of those. Cheap, and it cannot fail a file that would have compiled.</para>
    /// </summary>
    public static bool LooksLikeCode(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith("/*", StringComparison.Ordinal)) continue;

            return CodeStarts.Any(start => line.StartsWith(start, StringComparison.Ordinal));
        }

        // Comments only. Not a program, but not prose either — let the compiler have the last word.
        return true;
    }

    private static readonly string[] CodeStarts =
    [
        "using ", "namespace ", "#", "[",
        "public ", "internal ", "private ", "protected ",
        "sealed ", "abstract ", "static ", "partial ", "file ", "unsafe ",
        "class ", "record ", "struct ", "enum ", "interface ", "delegate ",
    ];
}
