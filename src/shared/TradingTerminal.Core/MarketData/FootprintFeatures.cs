using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Provenance / trust tag for a footprint feature set. Real exchange-classified trade tape
/// (initiator side known, or inferred from a true last-price + quote pair) carries full
/// diagnostic weight (q = 1.0). When no trade tape is available the extractor can synthesize
/// pseudo-prints from L1 quote/last updates via <see cref="FootprintFeatures.SyntheticPrints"/>;
/// those carry reduced weight (q ≈ 0.4) so downstream composites can discount the fallback path
/// instead of silently trusting it.
/// </summary>
public enum FeedQuality
{
    /// <summary>No usable feed.</summary>
    None = 0,

    /// <summary>Real trade tape (time and sales). Quality multiplier q = 1.0.</summary>
    RealTape = 1,

    /// <summary>Pseudo-prints synthesized from L1 quotes via the tick rule. Quality q ≈ 0.4.</summary>
    SyntheticL1 = 2,
}

/// <summary>Quality-multiplier lookup for <see cref="FeedQuality"/>.</summary>
public static class FeedQualityExtensions
{
    /// <summary>The dimensionless trust multiplier q ∈ [0, 1] for a feed quality.</summary>
    public static double Multiplier(this FeedQuality q) => q switch
    {
        FeedQuality.RealTape => 1.0,
        FeedQuality.SyntheticL1 => 0.4,
        _ => 0.0,
    };
}

/// <summary>
/// A single trade print reduced to the four fields the footprint extractor cares about:
/// price, size, aggressor side and event time. Decouples the extractor from the heavier
/// canonical <see cref="TradePrint"/> (and from broker plumbing) so it stays a pure helper.
/// </summary>
public readonly record struct FootprintPrint(double Price, long Size, AggressorSide Aggressor, DateTime TimeUtc)
{
    /// <summary>Projects a canonical <see cref="TradePrint"/> into the extractor's print shape.</summary>
    public static FootprintPrint From(TradePrint t) => new(t.Price, t.Size, t.Aggressor, t.EventTimeUtc);
}

/// <summary>
/// One price row of a footprint bar: buy- and sell-initiated volume that printed inside the
/// row's price bucket, plus the diagonal-imbalance flags computed against neighbouring rows.
/// </summary>
/// <param name="Price">Bucket reference price (the row's snapped price level).</param>
/// <param name="BuyVolume">Buy-initiated (ask-lifting) volume in this bucket.</param>
/// <param name="SellVolume">Sell-initiated (bid-hitting) volume in this bucket.</param>
/// <param name="BidImbalance">
/// Diagonal bid imbalance: this row's sell volume dominates the buy volume of the row one tick
/// <em>above</em> by at least the configured ratio. Flags absorption / support on the bid.
/// </param>
/// <param name="AskImbalance">
/// Diagonal ask imbalance: this row's buy volume dominates the sell volume of the row one tick
/// <em>below</em> by at least the configured ratio. Flags absorption / resistance on the ask.
/// </param>
/// <param name="ZeroBid">No sell-initiated volume printed in this bucket.</param>
/// <param name="ZeroAsk">No buy-initiated volume printed in this bucket.</param>
public sealed record FootprintFeatureRow(
    double Price,
    long BuyVolume,
    long SellVolume,
    bool BidImbalance,
    bool AskImbalance,
    bool ZeroBid,
    bool ZeroAsk)
{
    /// <summary>Total volume that printed in this bucket.</summary>
    public long TotalVolume => BuyVolume + SellVolume;

    /// <summary>Row delta: buy-initiated minus sell-initiated volume.</summary>
    public long Delta => BuyVolume - SellVolume;
}

/// <summary>
/// A completed footprint bar: per-price rows plus the bar-level aggregates that the v2 Apex
/// estimators consume — POC, total-volume centroid, the NEW buy- and sell-volume centroids
/// (volume-weighted price means per side, <em>not</em> argmax rows), the bar delta and the
/// running cumulative delta, plus stacked-imbalance run counts. Tagged with the
/// <see cref="FeedQuality"/> of the trade stream it was built from.
/// </summary>
/// <param name="StartUtc">Bar open time (UTC).</param>
/// <param name="EndUtc">Bar close time (UTC).</param>
/// <param name="Rows">Price rows, ordered high → low.</param>
/// <param name="PocPrice">Point of control: price of the row with the largest total volume (argmax).</param>
/// <param name="VolumeCentroid">Total-volume centroid: Σ(price·vol) / Σvol across all rows.</param>
/// <param name="BuyCentroid">
/// Buy-volume centroid b̄: Σ(price·buyVol) / ΣbuyVol — the buy-side VWAP across the bar's price
/// cells. NEW in v2; a centroid, not the argmax buy row.
/// </param>
/// <param name="SellCentroid">
/// Sell-volume centroid s̄: Σ(price·sellVol) / ΣsellVol — the sell-side VWAP across the bar's
/// price cells. NEW in v2; a centroid, not the argmax sell row.
/// </param>
/// <param name="BuyVolume">Total buy-initiated volume in the bar.</param>
/// <param name="SellVolume">Total sell-initiated volume in the bar.</param>
/// <param name="Delta">Bar delta: <see cref="BuyVolume"/> − <see cref="SellVolume"/>.</param>
/// <param name="CumulativeDelta">Running cumulative delta through the end of this bar.</param>
/// <param name="StackedBuy">Longest run of consecutive ask-imbalanced rows (stacked buying).</param>
/// <param name="StackedSell">Longest run of consecutive bid-imbalanced rows (stacked selling).</param>
/// <param name="Quality">Feed quality of the trade stream this bar was built from.</param>
public sealed record FootprintBar(
    DateTime StartUtc,
    DateTime EndUtc,
    IReadOnlyList<FootprintFeatureRow> Rows,
    double PocPrice,
    double VolumeCentroid,
    double BuyCentroid,
    double SellCentroid,
    long BuyVolume,
    long SellVolume,
    long Delta,
    long CumulativeDelta,
    int StackedBuy,
    int StackedSell,
    FeedQuality Quality)
{
    /// <summary>Total volume across all rows.</summary>
    public long TotalVolume => BuyVolume + SellVolume;
}

/// <summary>Tuning for the footprint feature extractor. All fields have v2 defaults.</summary>
/// <param name="ImbalanceRatio">Diagonal-imbalance ratio (default 3:1).</param>
public readonly record struct FootprintExtractorOptions(double ImbalanceRatio = 3.0)
{
    /// <summary>The v2 defaults. Constructed explicitly with the 3:1 ratio because a record-struct
    /// parameterless <c>new()</c> zero-initialises and does <em>not</em> apply the primary
    /// constructor's default value.</summary>
    public static FootprintExtractorOptions Default => new(ImbalanceRatio: 3.0);
}

/// <summary>
/// Stateless / streaming footprint feature extractor. Pure C#: no allocations beyond the result
/// rows, deterministic, and free of any broker / UI dependency. Reuses the
/// <see cref="Microstructure.ClassifyAggressor"/> Lee-Ready classifier for the synthetic-print
/// fallback rather than duplicating it.
///
/// <para>Conventions:</para>
/// <list type="bullet">
/// <item>A <b>row</b> is a price bucket of <c>tickSize</c> width; prints snap to
/// <c>round(price / tickSize) · tickSize</c>.</item>
/// <item>Buy volume is ask-lifting (<see cref="AggressorSide.Buy"/>); sell volume is bid-hitting
/// (<see cref="AggressorSide.Sell"/>). <see cref="AggressorSide.Unknown"/> prints are split
/// 50/50 so the bar's total volume is preserved.</item>
/// <item>Centroids are volume-weighted price means; <see cref="FootprintBar.PocPrice"/> is the
/// argmax-total-volume row.</item>
/// </list>
/// </summary>
public static class FootprintFeatures
{
    /// <summary>
    /// Builds one <see cref="FootprintBar"/> from a slice of prints. Pure: callers do the
    /// time/volume bucketing; this aggregates the slice into price rows + bar features.
    /// <paramref name="cumulativeDeltaBefore"/> threads the running cumulative delta from prior
    /// bars so the returned bar's <see cref="FootprintBar.CumulativeDelta"/> is correct.
    /// </summary>
    /// <param name="prints">Trade prints belonging to this bar (any order).</param>
    /// <param name="tickSize">Price-bucket width (must be &gt; 0).</param>
    /// <param name="startUtc">Bar open time.</param>
    /// <param name="endUtc">Bar close time.</param>
    /// <param name="quality">Feed quality tag carried onto the bar.</param>
    /// <param name="cumulativeDeltaBefore">Cumulative delta accumulated before this bar.</param>
    /// <param name="options">Imbalance ratio etc.</param>
    public static FootprintBar BuildBar(
        IReadOnlyList<FootprintPrint> prints,
        double tickSize,
        DateTime startUtc,
        DateTime endUtc,
        FeedQuality quality,
        long cumulativeDeltaBefore = 0,
        FootprintExtractorOptions options = default)
    {
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize), "tickSize must be positive.");
        if (options.Equals(default(FootprintExtractorOptions))) options = FootprintExtractorOptions.Default;

        // Bucket buy/sell volume per snapped price.
        var buy = new Dictionary<double, long>();
        var sell = new Dictionary<double, long>();
        foreach (var p in prints)
        {
            if (p.Size <= 0) continue;
            var bucket = Math.Round(p.Price / tickSize, MidpointRounding.AwayFromZero) * tickSize;
            switch (p.Aggressor)
            {
                case AggressorSide.Buy:
                    buy[bucket] = buy.GetValueOrDefault(bucket) + p.Size;
                    break;
                case AggressorSide.Sell:
                    sell[bucket] = sell.GetValueOrDefault(bucket) + p.Size;
                    break;
                default:
                    // Unknown: split so total volume is preserved (half rounds up to the buy side).
                    var half = p.Size / 2;
                    buy[bucket] = buy.GetValueOrDefault(bucket) + (p.Size - half);
                    sell[bucket] = sell.GetValueOrDefault(bucket) + half;
                    break;
            }
        }

        // Union of price levels, sorted high → low.
        var prices = new SortedSet<double>(buy.Keys, DescendingComparer.Instance);
        foreach (var k in sell.Keys) prices.Add(k);

        long totalBuy = 0, totalSell = 0;
        double pocPrice = 0, volNum = 0, buyNum = 0, sellNum = 0;
        long bestRowVol = -1;

        // First pass: aggregates + POC + centroid numerators.
        foreach (var price in prices)
        {
            var b = buy.GetValueOrDefault(price);
            var s = sell.GetValueOrDefault(price);
            totalBuy += b;
            totalSell += s;
            var rowTotal = b + s;
            volNum += price * rowTotal;
            buyNum += price * b;
            sellNum += price * s;
            if (rowTotal > bestRowVol) { bestRowVol = rowTotal; pocPrice = price; }
        }

        var totalVol = totalBuy + totalSell;
        var volumeCentroid = totalVol > 0 ? volNum / totalVol : 0;
        var buyCentroid = totalBuy > 0 ? buyNum / totalBuy : 0;
        var sellCentroid = totalSell > 0 ? sellNum / totalSell : 0;

        // Second pass: build rows with diagonal-imbalance flags. The diagonal rule compares this
        // row's bid (sell) volume to the ask (buy) volume of the row one tick above, and this
        // row's ask (buy) volume to the bid (sell) volume of the row one tick below.
        var ordered = prices.ToList(); // already high → low
        var rows = new List<FootprintFeatureRow>(ordered.Count);
        var ratio = options.ImbalanceRatio;
        for (var i = 0; i < ordered.Count; i++)
        {
            var price = ordered[i];
            var b = buy.GetValueOrDefault(price);
            var s = sell.GetValueOrDefault(price);

            // Row above (higher price) is i-1 in high→low order; row below is i+1.
            long buyAbove = i > 0 ? buy.GetValueOrDefault(ordered[i - 1]) : 0;
            long sellBelow = i < ordered.Count - 1 ? sell.GetValueOrDefault(ordered[i + 1]) : 0;

            var bidImb = s > 0 && s >= buyAbove * ratio;
            var askImb = b > 0 && b >= sellBelow * ratio;

            rows.Add(new FootprintFeatureRow(price, b, s, bidImb, askImb, ZeroBid: s == 0, ZeroAsk: b == 0));
        }

        var (stackedBuy, stackedSell) = StackedRuns(rows);
        var delta = totalBuy - totalSell;

        return new FootprintBar(
            startUtc, endUtc, rows, pocPrice, volumeCentroid, buyCentroid, sellCentroid,
            totalBuy, totalSell, delta, cumulativeDeltaBefore + delta,
            stackedBuy, stackedSell, quality);
    }

    /// <summary>
    /// Longest consecutive run of ask-imbalanced rows (stacked buying) and of bid-imbalanced rows
    /// (stacked selling). Rows are assumed ordered high → low; runs are scanned over that order.
    /// </summary>
    private static (int StackedBuy, int StackedSell) StackedRuns(IReadOnlyList<FootprintFeatureRow> rows)
    {
        int buyRun = 0, buyMax = 0, sellRun = 0, sellMax = 0;
        foreach (var r in rows)
        {
            if (r.AskImbalance) { buyRun++; if (buyRun > buyMax) buyMax = buyRun; } else buyRun = 0;
            if (r.BidImbalance) { sellRun++; if (sellRun > sellMax) sellMax = sellRun; } else sellRun = 0;
        }
        return (buyMax, sellMax);
    }

    /// <summary>
    /// Suggested price-bucket size for a footprint bar so it shows roughly
    /// <paramref name="targetRows"/> rows: <c>tickSize ≈ ATR_bar / targetRows</c>, then snapped up
    /// to a multiple of the instrument <paramref name="instrumentTickSize"/> and clamped to at
    /// least one tick. Callers may override the result. Falls back to the instrument tick when ATR
    /// is non-positive.
    /// </summary>
    /// <param name="barAtr">Average true range over a bar (price units).</param>
    /// <param name="instrumentTickSize">Minimum price increment of the instrument (price units).</param>
    /// <param name="targetRows">Desired rows per bar (default 20).</param>
    public static double SuggestRowSize(double barAtr, double instrumentTickSize, int targetRows = 20)
    {
        if (instrumentTickSize <= 0) throw new ArgumentOutOfRangeException(nameof(instrumentTickSize));
        if (targetRows <= 0) throw new ArgumentOutOfRangeException(nameof(targetRows));
        if (barAtr <= 0 || double.IsNaN(barAtr)) return instrumentTickSize;

        var raw = barAtr / targetRows;
        // Snap to a whole multiple of the instrument tick, at least one tick.
        var multiples = Math.Max(1, (long)Math.Round(raw / instrumentTickSize, MidpointRounding.AwayFromZero));
        return multiples * instrumentTickSize;
    }

    /// <summary>
    /// Synthesizes pseudo trade-prints from a sequence of L1 quote/last updates via the
    /// Lee-Ready / tick-rule classifier (<see cref="Microstructure.ClassifyAggressor"/>). This is
    /// the diagnostic fallback path used when no real trade tape exists: each update is treated as
    /// one print at the last/trade price, with size proxied by the caller's supplied size and
    /// aggressor inferred from the prevailing quote and the prior trade price. Output is tagged
    /// <see cref="FeedQuality.SyntheticL1"/> by the consumer.
    /// </summary>
    /// <param name="updates">
    /// L1 updates as (tradePrice, size, bid, ask, timeUtc). For a quote-only feed the caller passes
    /// the mid (or last touch) as the trade price and a size proxy (e.g. BidSize + AskSize).
    /// </param>
    public static IEnumerable<FootprintPrint> SyntheticPrints(
        IEnumerable<(double TradePrice, long Size, double Bid, double Ask, DateTime TimeUtc)> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        double priorTrade = 0;
        var priorClass = AggressorSide.Unknown;
        foreach (var u in updates)
        {
            if (u.Size <= 0 || u.TradePrice <= 0) continue;
            var side = Microstructure.ClassifyAggressor(u.TradePrice, u.Bid, u.Ask, priorTrade, priorClass);
            yield return new FootprintPrint(u.TradePrice, u.Size, side, u.TimeUtc);
            priorTrade = u.TradePrice;
            if (side != AggressorSide.Unknown) priorClass = side;
        }
    }

    private sealed class DescendingComparer : IComparer<double>
    {
        public static readonly DescendingComparer Instance = new();
        public int Compare(double x, double y) => y.CompareTo(x);
    }
}
