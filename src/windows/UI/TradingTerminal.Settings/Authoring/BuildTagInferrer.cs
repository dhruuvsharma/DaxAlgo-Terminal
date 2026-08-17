using System.Text.RegularExpressions;

namespace TradingTerminal.App.Authoring;

/// <summary>
/// Turns free-text chat into short build tags so the workbench shows what the user is making
/// (direction, signal family, data need, legs) before Register — same idea as catalog CustomTags.
/// </summary>
internal static partial class BuildTagInferrer
{
    /// <summary>Merge tags inferred from <paramref name="text"/> into <paramref name="target"/> (no dupes).</summary>
    public static void Absorb(string? text, ICollection<string> target)
    {
        if (string.IsNullOrWhiteSpace(text) || target is null) return;
        foreach (var tag in Infer(text))
        {
            if (target.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))) continue;
            target.Add(tag);
        }
    }

    public static IReadOnlyList<string> Infer(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return [];

        var tags = new List<string>();
        void Add(string tag)
        {
            if (tags.Any(x => x.Equals(tag, StringComparison.OrdinalIgnoreCase))) return;
            tags.Add(tag);
        }

        var lower = t.ToLowerInvariant();

        if (lower.Contains("default", StringComparison.Ordinal))
        {
            Add("ema-cross");
            Add("long/short");
            Add("L1");
            Add("1-instrument");
            Add("reverse-on-signal");
            Add("qty-1");
        }

        if (LooksLike(lower, "long/short", "long short", "long-short", "both sides", "always in"))
            Add("long/short");
        else if (LooksLike(lower, "long only", "long-only", "buys only"))
            Add("long-only");
        else if (LooksLike(lower, "short only", "short-only", "sells only"))
            Add("short-only");

        if (LooksLike(lower, "ema cross", "ema-cross", "moving average cross", "fast crosses slow"))
            Add("ema-cross");
        if (LooksLike(lower, "breakout", "n-bar high", "n-bar low", "donchian"))
            Add("breakout");
        if (LooksLike(lower, "mean reversion", "mean-reversion", "fade", "stretched", "z-score"))
            Add("mean-reversion");
        if (LooksLike(lower, "momentum", "trend follow"))
            Add("momentum");
        if (LooksLike(lower, "liquidity sweep", "stop-run", "stop run", "sweep"))
            Add("liquidity-sweep");
        if (LooksLike(lower, "order flow", "order-flow", "absorption", "delta diverg"))
            Add("order-flow");
        if (LooksLike(lower, "cumulative-delta", "cum delta", "cvd"))
            Add("order-flow");

        if (LooksLike(lower, "trade tape", "prints", "tick-driven", "tick driven", "l1 mid", "mid-price", "mid price"))
            Add("Trade tape");
        if (LooksLike(lower, "order book", "l2", "depth", "obi"))
            Add("Depth");
        if (LooksLike(lower, "1-min", "1 min", "1m bar", "5-min", "5 min", "bar-based", "ohlc", "candles"))
            Add("Bars");
        if (LooksLike(lower, "l1 quote", "bid/ask", "top of book") && !tags.Contains("Trade tape"))
            Add("L1");

        if (LooksLike(lower, "multi-leg", "multi leg", "pairs", "spread", "arb", "two instrument", "2 instrument"))
            Add("multi-leg");
        else if (LooksLike(lower, "single instrument", "1 instrument", "one instrument", "one symbol"))
            Add("1-instrument");

        if (LooksLike(lower, "reverse on signal", "reverse-on-signal", "flip on", "always in the market"))
            Add("reverse-on-signal");
        if (LooksLike(lower, "stop", "target", "take profit", "tp/", "sl/"))
            Add("stops/targets");

        var qty = QtyPattern().Match(t);
        if (qty.Success) Add($"qty-{qty.Groups[1].Value}");

        foreach (Match m in SymbolPattern().Matches(t))
        {
            var sym = m.Groups[1].Value.ToUpperInvariant();
            if (sym is "EMA" or "SMA" or "RSI" or "VWAP" or "VPOC" or "MDD" or "API") continue;
            Add(sym);
        }

        if (LooksLike(lower, "btcusdt", "btc/usdt", "binance btc")) Add("BTCUSDT");
        if (LooksLike(lower, " ethusdt", "eth/usdt") || lower.StartsWith("ethusdt", StringComparison.Ordinal))
            Add("ETHUSDT");
        if (LooksLike(lower, " es futures", " /es", "e-mini", "emini") || Regex.IsMatch(lower, @"\bes\b.*futur"))
            Add("ES");

        return tags;
    }

    private static bool LooksLike(string lower, params string[] needles) =>
        needles.Any(n => lower.Contains(n, StringComparison.Ordinal));

    [GeneratedRegex(@"\bqty\s*[:=]?\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QtyPattern();

    // Tickers like BTCUSDT, AAPL — avoid tiny noise words via length.
    [GeneratedRegex(@"\b([A-Z]{1,5}USDT|[A-Z]{2,5})\b", RegexOptions.CultureInvariant)]
    private static partial Regex SymbolPattern();
}
