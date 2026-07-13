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

    private static readonly string[] CSharpLanguages = ["csharp", "cs", "c#", ""];

    /// <summary>The first C# block (or the whole reply when unfenced) — the single-file path.</summary>
    public static string Extract(string? reply)
    {
        var files = ExtractFiles(reply);
        return files.Count > 0 ? files[0].Content : string.Empty;
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
}
