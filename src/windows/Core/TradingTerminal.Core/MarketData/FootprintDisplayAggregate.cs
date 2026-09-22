namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Display-time aggregation of footprint rows (Bookmap vertical smart scaling): merge fine
/// instrument-tick cells into coarser display ticks without rebuilding the live bucketer.
/// </summary>
public static class FootprintDisplayAggregate
{
    /// <summary>
    /// Merges <paramref name="rows"/> (high→low) onto <paramref name="displayTick"/> buckets.
    /// Diagonal imbalance flags are recomputed on the merged grid.
    /// </summary>
    public static IReadOnlyList<FootprintFeatureRow> MergeRows(
        IReadOnlyList<FootprintFeatureRow> rows,
        double displayTick,
        int cryptoDecimals = -1,
        double imbalanceRatio = 3.0)
    {
        if (rows.Count == 0 || displayTick <= 0) return rows;

        var buy = new Dictionary<double, long>();
        var sell = new Dictionary<double, long>();
        foreach (var r in rows)
        {
            var key = FootprintPriceSnap.Snap(r.Price, displayTick, cryptoDecimals);
            buy[key] = buy.GetValueOrDefault(key) + r.BuyVolume;
            sell[key] = sell.GetValueOrDefault(key) + r.SellVolume;
        }

        var prices = buy.Keys.Concat(sell.Keys).Distinct().OrderByDescending(p => p).ToList();
        var merged = new List<FootprintFeatureRow>(prices.Count);
        for (var i = 0; i < prices.Count; i++)
        {
            var price = prices[i];
            var b = buy.GetValueOrDefault(price);
            var s = sell.GetValueOrDefault(price);
            long buyAbove = i > 0 ? buy.GetValueOrDefault(prices[i - 1]) : 0;
            long sellBelow = i < prices.Count - 1 ? sell.GetValueOrDefault(prices[i + 1]) : 0;
            var bidImb = s > 0 && s >= buyAbove * imbalanceRatio;
            var askImb = b > 0 && b >= sellBelow * imbalanceRatio;
            merged.Add(new FootprintFeatureRow(price, b, s, bidImb, askImb, s == 0, b == 0));
        }
        return merged;
    }
}
