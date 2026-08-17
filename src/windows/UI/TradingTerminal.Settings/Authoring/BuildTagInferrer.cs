using System.Text.RegularExpressions;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// Build tags = DaxAlgo <see cref="StrategyDataRequirement"/> chips only — same set Strategy Studio
/// shows (L1 / BAR / L2 / TAPE). They name what tools/data the strategy needs, not which alpha family
/// it is. Optional free-form <c>TAGS:</c> lines still merge, but data chips are the product surface.
/// </summary>
internal static partial class BuildTagInferrer
{
    public const string TagL1 = "L1";
    public const string TagBars = "BAR";
    public const string TagDepth = "L2";
    public const string TagTape = "TAPE";

    public static void Absorb(string? text, ICollection<string> target)
    {
        if (string.IsNullOrWhiteSpace(text) || target is null) return;
        foreach (var tag in Infer(text))
            Add(target, tag);
    }

    public static void AbsorbDataRequirement(StrategyDataRequirement requirement, ICollection<string> target)
    {
        if (target is null) return;
        foreach (var tag in FromDataRequirement(requirement))
            Add(target, tag);
    }

    /// <summary>Same pills Strategy Studio uses via <c>StrategyTagsConverter</c>.</summary>
    public static IReadOnlyList<string> FromDataRequirement(StrategyDataRequirement requirement)
    {
        var tags = new List<string>();
        if (requirement.HasFlag(StrategyDataRequirement.L1)) tags.Add(TagL1);
        if (requirement.HasFlag(StrategyDataRequirement.Bars)) tags.Add(TagBars);
        if (requirement.HasFlag(StrategyDataRequirement.Depth)) tags.Add(TagDepth);
        if (requirement.HasFlag(StrategyDataRequirement.TradeTape)) tags.Add(TagTape);
        return tags;
    }

    public static IReadOnlyList<string> Infer(string text)
    {
        var tags = new List<string>();

        // Free-form TAGS: — keep aliases that mean data needs; map synonyms → Studio chips.
        foreach (Match m in TagsLinePattern().Matches(text))
        {
            foreach (var part in m.Groups[1].Value.Split(
                         [',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (NormalizeDataTag(part) is { } data)
                    Add(tags, data);
                else if (part.Length is >= 1 and <= 48)
                    Add(tags, part.Trim()); // optional idea label; not required
            }
        }

        var lower = text.ToLowerInvariant();
        if (ContainsAny(lower, "trade tape", "prints", "tick-driven", "tick driven", "footprint", "time and sales"))
            Add(tags, TagTape);
        if (ContainsAny(lower, "order book", "orderbook", "depth", "l2", "obi", "heatmap"))
            Add(tags, TagDepth);
        if (ContainsAny(lower, "1-min", "1 min", "5-min", "5 min", "bar-based", "ohlc", "candles", "chart"))
            Add(tags, TagBars);
        if (ContainsAny(lower, "l1", "bid/ask", "mid-price", "mid price", "top of book", "quote"))
            Add(tags, TagL1);

        return tags;
    }

    /// <summary>Map common spellings onto the four Studio chips.</summary>
    public static string? NormalizeDataTag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        var key = t.ToLowerInvariant().Replace(' ', '-');
        return key switch
        {
            "l1" or "quotes" or "quote" => TagL1,
            "bar" or "bars" or "ohlc" or "candles" or "chart" or "charts" => TagBars,
            "l2" or "depth" or "orderbook" or "order-book" or "book" => TagDepth,
            "tape" or "trade-tape" or "tradetape" or "prints" or "footprint" => TagTape,
            _ => null,
        };
    }

    private static void Add(ICollection<string> target, string tag)
    {
        var t = tag.Trim();
        if (t.Length == 0) return;
        if (target.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase))) return;
        target.Add(t);
    }

    private static bool ContainsAny(string lower, params string[] needles) =>
        needles.Any(n => lower.Contains(n, StringComparison.Ordinal));

    [GeneratedRegex(@"^\s*TAGS?\s*:\s*(.+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TagsLinePattern();
}
