using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Stateless microstructure helpers. All functions are pure: same inputs → same outputs,
/// no allocations except where noted. Used by HFT-flavoured strategies; live in Core so
/// they don't accidentally pull in WPF / broker dependencies.
/// </summary>
public static class Microstructure
{
    /// <summary>
    /// Size-weighted "fair price" between bid and ask. The intuition: the side with smaller
    /// resting size is the side likely to move first, so the fair price leans toward the
    /// thinner side. Formally: <c>(bidSize * ask + askSize * bid) / (bidSize + askSize)</c>.
    /// Falls back to the simple mid when either size is zero or unknown.
    /// </summary>
    public static double Microprice(double bid, double ask, long bidSize, long askSize)
    {
        var total = bidSize + askSize;
        if (total <= 0) return (bid + ask) * 0.5;
        return ((double)bidSize * ask + (double)askSize * bid) / total;
    }

    /// <inheritdoc cref="Microprice(double,double,long,long)"/>
    public static double Microprice(Tick t) => Microprice(t.Bid, t.Ask, t.BidSize, t.AskSize);

    /// <summary>
    /// Queue imbalance in [-1, 1]: <c>(bidSize - askSize) / (bidSize + askSize)</c>.
    /// Positive ⇒ more depth on the bid (buying pressure); negative ⇒ more on the ask.
    /// </summary>
    public static double QueueImbalance(long bidSize, long askSize)
    {
        var total = bidSize + askSize;
        if (total <= 0) return 0;
        return (double)(bidSize - askSize) / total;
    }

    /// <inheritdoc cref="QueueImbalance(long,long)"/>
    public static double QueueImbalance(Tick t) => QueueImbalance(t.BidSize, t.AskSize);

    /// <summary>Half-spread in price units: <c>(ask - bid) / 2</c>.</summary>
    public static double HalfSpread(double bid, double ask) => (ask - bid) * 0.5;

    /// <summary>Half-spread in price units for a tick.</summary>
    public static double HalfSpread(Tick t) => HalfSpread(t.Bid, t.Ask);

    // ── Multi-level (L2) helpers ─────────────────────────────────────────────────────────
    //
    // These work on a <see cref="DepthSnapshot"/> — Bids sorted descending, Asks ascending.
    // When the snapshot is empty on one side, "<side> total" defaults to 0; ratios that
    // would divide by zero return a sensible neutral (0 or the mid). All functions are O(N)
    // where N is the level count.

    /// <summary>
    /// Multi-level queue imbalance: cumulative bid size minus cumulative ask size, divided
    /// by the sum, taken across at most <paramref name="depthLevels"/> top levels on each
    /// side. Returns a value in [-1, 1]; positive ⇒ heavier bid book.
    /// </summary>
    public static double CumulativeImbalance(DepthSnapshot snapshot, int depthLevels = 5)
    {
        long bidSum = 0, askSum = 0;
        for (var i = 0; i < Math.Min(depthLevels, snapshot.Bids.Count); i++) bidSum += snapshot.Bids[i].Size;
        for (var i = 0; i < Math.Min(depthLevels, snapshot.Asks.Count); i++) askSum += snapshot.Asks[i].Size;
        var total = bidSum + askSum;
        return total <= 0 ? 0 : (double)(bidSum - askSum) / total;
    }

    /// <summary>
    /// Size-weighted mid across all (or top <paramref name="depthLevels"/>) levels:
    /// <c>(Σ bidSize·bidPrice + Σ askSize·askPrice) / Σ all sizes</c>. Generalises the
    /// L1 microprice to the full visible book. Falls back to <see cref="DepthSnapshot.BestBid"/>
    /// / <see cref="DepthSnapshot.BestAsk"/> mid when the book is empty.
    /// </summary>
    public static double WeightedMidPrice(DepthSnapshot snapshot, int depthLevels = 5)
    {
        double notional = 0; long sizeSum = 0;
        for (var i = 0; i < Math.Min(depthLevels, snapshot.Bids.Count); i++)
        {
            var lvl = snapshot.Bids[i];
            notional += lvl.Price * lvl.Size;
            sizeSum += lvl.Size;
        }
        for (var i = 0; i < Math.Min(depthLevels, snapshot.Asks.Count); i++)
        {
            var lvl = snapshot.Asks[i];
            notional += lvl.Price * lvl.Size;
            sizeSum += lvl.Size;
        }
        if (sizeSum <= 0) return (snapshot.BestBid + snapshot.BestAsk) * 0.5;
        return notional / sizeSum;
    }

    /// <summary>
    /// Total resting size on a single side across the top <paramref name="depthLevels"/>.
    /// "Book pressure" as Cartea-Jaimungal use the term in their HFT papers.
    /// </summary>
    public static long SideDepth(IReadOnlyList<DepthLevel> side, int depthLevels = 5)
    {
        long s = 0;
        for (var i = 0; i < Math.Min(depthLevels, side.Count); i++) s += side[i].Size;
        return s;
    }

    /// <summary>
    /// Estimated slippage in price units when sweeping <paramref name="quantity"/> through
    /// the requested side of the book. Walks levels in order, accumulating filled size
    /// until <paramref name="quantity"/> is consumed; returns the average fill price minus
    /// the touch (so positive means cost vs the touch). When the book is shallower than
    /// the requested size, returns the worst-level price difference; callers can detect
    /// insufficient liquidity via <paramref name="fullyFilled"/>.
    /// </summary>
    public static double EstimatedSlippage(
        IReadOnlyList<DepthLevel> side,
        long quantity,
        out bool fullyFilled)
    {
        fullyFilled = false;
        if (side.Count == 0 || quantity <= 0) return 0;
        long remaining = quantity;
        double notional = 0;
        var touch = side[0].Price;
        foreach (var lvl in side)
        {
            var take = Math.Min(remaining, lvl.Size);
            notional += lvl.Price * take;
            remaining -= take;
            if (remaining <= 0) { fullyFilled = true; break; }
        }
        var filled = quantity - remaining;
        if (filled <= 0) return 0;
        var avg = notional / filled;
        return Math.Abs(avg - touch);
    }

    /// <summary>
    /// Largest "gap" between consecutive price levels on one side, expressed as a price
    /// difference. A wide gap ⇒ thin book ⇒ slippage spikes when a sweep crosses it.
    /// Returns 0 for a book with fewer than 2 levels.
    /// </summary>
    public static double LargestLevelGap(IReadOnlyList<DepthLevel> side)
    {
        var max = 0d;
        for (var i = 1; i < side.Count; i++)
        {
            var gap = Math.Abs(side[i].Price - side[i - 1].Price);
            if (gap > max) max = gap;
        }
        return max;
    }

    /// <summary>
    /// Lee-Ready (1991) trade-aggressor classifier. Most brokers (IB, NT) don't report
    /// initiating side on the trade tape, so the standard inference is:
    /// <list type="number">
    /// <item><b>Quote rule</b> — if the trade prints at or above the prevailing best ask,
    /// it lifted the offer (buy-initiated). At or below the best bid, it hit the bid
    /// (sell-initiated).</item>
    /// <item><b>Tick rule</b> — when the trade prints inside the spread the quote rule
    /// is ambiguous; compare to the previous trade price: higher than prior ⇒ buy,
    /// lower ⇒ sell, equal ⇒ carry the prior classification forward (zero-tick rule).</item>
    /// </list>
    /// Pass <paramref name="priorTradePrice"/> = 0 (the default) and <paramref name="priorClassification"/>
    /// = <see cref="AggressorSide.Unknown"/> for the first trade in a session — ambiguous prints
    /// then return <see cref="AggressorSide.Unknown"/> rather than guessing.
    /// </summary>
    public static AggressorSide ClassifyAggressor(
        double tradePrice, double bid, double ask,
        double priorTradePrice = 0,
        AggressorSide priorClassification = AggressorSide.Unknown)
    {
        // Quote rule (also handles the degenerate case where bid >= ask — locked/crossed book —
        // by deferring to the tick rule rather than producing nonsense from the comparison).
        if (ask > bid)
        {
            if (tradePrice >= ask) return AggressorSide.Buy;
            if (tradePrice <= bid) return AggressorSide.Sell;
        }

        // Tick rule.
        if (priorTradePrice <= 0) return AggressorSide.Unknown;
        if (tradePrice > priorTradePrice) return AggressorSide.Buy;
        if (tradePrice < priorTradePrice) return AggressorSide.Sell;
        return priorClassification; // zero-tick: carry forward
    }
}
