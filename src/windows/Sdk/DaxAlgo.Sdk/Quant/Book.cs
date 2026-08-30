using TradingTerminal.Core.Domain;

namespace DaxAlgo.Sdk.Quant;

/// <summary>
/// What the resting book says about where the next trade will happen — the statistics that turn a
/// depth snapshot into a number a strategy can act on.
/// </summary>
public static class Book
{
    /// <summary>
    /// The size-weighted mid: the fair value implied by the queues rather than by the prices alone.
    ///
    /// <para>The plain mid says the same thing whether there are ten lots bid against a thousand
    /// offered or the reverse, which is precisely the case where the next print is predictable. The
    /// microprice leans toward the thin side, because that is the side about to be taken out — it is
    /// the single most useful line to draw over a book, and the reference a spread or edge should be
    /// measured from.</para>
    /// </summary>
    public static double Microprice(double bidPrice, double bidSize, double askPrice, double askSize)
    {
        var total = bidSize + askSize;
        if (total <= 0d || !double.IsFinite(bidPrice) || !double.IsFinite(askPrice))
            return (bidPrice + askPrice) * 0.5d;

        // Weighted by the OPPOSITE side's size: a large bid queue means the price is pinned near the
        // offer, not near the bid.
        return ((bidPrice * askSize) + (askPrice * bidSize)) / total;
    }

    /// <summary>The microprice of a quote.</summary>
    public static double Microprice(Quote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        return Microprice(quote.Bid, quote.BidSize, quote.Ask, quote.AskSize);
    }

    /// <summary>The microprice at the top of a depth snapshot.</summary>
    public static double Microprice(DepthSnapshot depth)
    {
        ArgumentNullException.ThrowIfNull(depth);
        return Microprice(depth.BestBid, depth.BestBidSize, depth.BestAsk, depth.BestAskSize);
    }

    /// <summary>
    /// Queue imbalance at the touch, in [-1, 1] — positive when the bid is larger.
    ///
    /// <para>The most predictive single number in a limit order book at the shortest horizons, and
    /// also the most over-traded: it mean-reverts as fast as it predicts, so it belongs in the timing
    /// of an entry that something else justified rather than as the reason for one.</para>
    /// </summary>
    public static double Imbalance(double bidSize, double askSize) =>
        Num.Clamp(Num.SafeDiv(bidSize - askSize, bidSize + askSize), -1d, 1d);

    /// <summary>Queue imbalance at the touch of a quote.</summary>
    public static double Imbalance(Quote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        return Imbalance(quote.BidSize, quote.AskSize);
    }

    /// <summary>
    /// Imbalance over the first <paramref name="levels"/> of each side.
    ///
    /// <para>Deeper than the touch on purpose: the top of book is where the games are played, and an
    /// imbalance that survives five levels is closer to real intent than one that a single order can
    /// create and cancel.</para>
    /// </summary>
    public static double Imbalance(DepthSnapshot depth, int levels)
    {
        ArgumentNullException.ThrowIfNull(depth);
        return Imbalance(DepthTotal(depth.Bids, levels), DepthTotal(depth.Asks, levels));
    }

    /// <summary>Resting size over the first <paramref name="levels"/> of one side.</summary>
    public static double DepthTotal(IReadOnlyList<DepthLevel>? side, int levels)
    {
        if (side is null || side.Count == 0 || levels <= 0) return 0d;

        var total = 0d;
        var take = Math.Min(levels, side.Count);
        for (var i = 0; i < take; i++) total += side[i].Size;
        return total;
    }

    /// <summary>
    /// What it costs to take <paramref name="units"/> immediately, as a size-weighted price walking
    /// the book.
    ///
    /// <para>Returns zero when the side cannot fill the order at all, which is a distinct answer from
    /// an expensive fill and must not be treated as a cheap one: a strategy that sizes off this number
    /// needs to know the difference between "costly" and "impossible".</para>
    /// </summary>
    /// <param name="side">Asks to buy, bids to sell.</param>
    /// <param name="units">How many units to fill.</param>
    public static double SweepPrice(IReadOnlyList<DepthLevel>? side, double units)
    {
        if (side is null || side.Count == 0 || units <= 0d) return 0d;

        var remaining = units;
        var notional = 0d;
        foreach (var level in side)
        {
            var take = Math.Min(remaining, level.Size);
            notional += take * level.Price;
            remaining -= take;
            if (remaining <= 0d) return Num.SafeDiv(notional, units);
        }

        return 0d;
    }

    /// <summary>The slippage a sweep of <paramref name="units"/> would pay against
    /// <paramref name="reference"/>, in price units. Zero when the book cannot fill it.</summary>
    public static double SweepSlippage(IReadOnlyList<DepthLevel>? side, double units, double reference)
    {
        var swept = SweepPrice(side, units);
        return swept <= 0d ? 0d : Math.Abs(swept - reference);
    }
}

/// <summary>
/// Rolling statistics of the bid-ask spread, so "the spread blew out" can be said in the
/// instrument's own terms.
///
/// <para>A spread threshold written as a tick count is a threshold about one instrument on one day.
/// The same widening that is unremarkable at the open is a liquidity event at midday, and a rule that
/// cannot tell them apart either trades through the event or refuses to trade all morning. Comparing
/// against the recent distribution is what makes a single rule portable.</para>
/// </summary>
public sealed class SpreadStats : IEstimator
{
    private readonly RollingWindow _spreads;

    /// <param name="period">How many quotes to measure over.</param>
    public SpreadStats(int period = 200) => _spreads = new RollingWindow(period);

    /// <summary>The window length in quotes.</summary>
    public int Period => _spreads.Capacity;

    /// <summary>The most recent spread.</summary>
    public double Value { get; private set; }

    /// <inheritdoc/>
    public bool IsReady => _spreads.IsFull;

    /// <summary>Mean spread over the window.</summary>
    public double Mean => _spreads.Mean;

    /// <summary>Standard deviation of the spread over the window.</summary>
    public double StandardDeviation => _spreads.StandardDeviation;

    /// <summary>The median spread — the "normal" one, and unlike the mean it is not dragged by the
    /// handful of dislocations the strategy most wants to detect.</summary>
    public double Median => _spreads.Median();

    /// <summary>How many standard deviations the current spread sits above its recent mean.</summary>
    public double ZScore => _spreads.ZScoreOf(Value);

    /// <summary>True when the current spread is more than <paramref name="deviations"/> standard
    /// deviations wide. False until the window has filled, so a strategy does not refuse to trade
    /// its first two hundred quotes.</summary>
    public bool IsWide(double deviations = 2d) => IsReady && ZScore > deviations;

    /// <summary>Records one spread and returns it.</summary>
    public double Update(double spread)
    {
        if (!double.IsFinite(spread) || spread < 0d) return Value;

        Value = spread;
        _spreads.Update(spread);
        return Value;
    }

    /// <summary>Records the spread of a quote.</summary>
    public double Update(Quote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        return Update(quote.Spread);
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _spreads.Reset();
        Value = 0d;
    }
}
