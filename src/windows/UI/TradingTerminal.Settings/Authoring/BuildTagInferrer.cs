using System.Text.RegularExpressions;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// Build tags are free-form labels for the workbench — they do <b>not</b> enumerate strategies.
/// Sources:
/// <list type="number">
/// <item>Explicit <c>TAGS:</c> lines from the model or user (any words — no whitelist).</item>
/// <item>Canonical DaxAlgo data tags from <see cref="StrategyDataRequirement"/> (L1 / Bars / Depth / Trade tape).</item>
/// <item>Light phrase hints that only map chat → those same data tags (optional helper).</item>
/// </list>
/// </summary>
internal static partial class BuildTagInferrer
{
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

    public static IReadOnlyList<string> FromDataRequirement(StrategyDataRequirement requirement)
    {
        var tags = new List<string>();
        if (requirement.HasFlag(StrategyDataRequirement.L1)) tags.Add("L1");
        if (requirement.HasFlag(StrategyDataRequirement.Bars)) tags.Add("Bars");
        if (requirement.HasFlag(StrategyDataRequirement.Depth)) tags.Add("Depth");
        if (requirement.HasFlag(StrategyDataRequirement.TradeTape)) tags.Add("Trade tape");
        return tags;
    }

    public static IReadOnlyList<string> Infer(string text)
    {
        var tags = new List<string>();
        void AddLocal(string tag) => Add(tags, tag);

        // 1) Free-form: TAGS: a, b, c  (model or user — unlimited vocabulary)
        foreach (Match m in TagsLinePattern().Matches(text))
        {
            foreach (var part in m.Groups[1].Value.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Length is < 1 or > 48) continue;
                AddLocal(part);
            }
        }

        // 2) Soft hints → only existing DaxAlgo data tags (does not invent strategy families)
        var lower = text.ToLowerInvariant();
        if (ContainsAny(lower, "trade tape", "prints", "tick-driven", "tick driven", "absorption", "footprint"))
            AddLocal("Trade tape");
        if (ContainsAny(lower, "order book", "depth", "l2", "obi", "heatmap"))
            AddLocal("Depth");
        if (ContainsAny(lower, "1-min", "1 min", "5-min", "5 min", "bar-based", "ohlc", "candles", "ema cross", "breakout"))
            AddLocal("Bars");
        if (ContainsAny(lower, "l1", "bid/ask", "mid-price", "mid price", "top of book"))
            AddLocal("L1");

        return tags;
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
