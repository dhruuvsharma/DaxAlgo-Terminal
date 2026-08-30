using TradingTerminal.Core.Domain;

namespace DaxAlgo.Sdk.Quant;

/// <summary>Which side crossed the spread to make a trade happen.</summary>
public enum TradeSide
{
    /// <summary>Neither rule could decide — a print at the mid with no prior move.</summary>
    Unknown = 0,

    /// <summary>A buyer lifted the offer.</summary>
    Buy,

    /// <summary>A seller hit the bid.</summary>
    Sell,
}

/// <summary>
/// Deciding who was the aggressor on a print, for feeds that do not say.
///
/// <para>Every order-flow statistic downstream is built on this one classification, so it is worth
/// getting right rather than assuming. Use the quote rule where a quote is available — a print at or
/// above the offer was a buy, at or below the bid a sell, and the Lee-Ready convention breaks the
/// remaining ties by comparing against the mid. Fall back to the tick rule only when there is no
/// quote: it is noticeably worse in fast markets, where the last print is stale by the time the next
/// one arrives.</para>
///
/// <para><see cref="TradePrint.Aggressor"/> is authoritative when the venue reports it. Classify only
/// when it is <see cref="AggressorSide.Unknown"/>.</para>
/// </summary>
public static class TradeClassifier
{
    /// <summary>The venue's own answer where it has one, and the quote rule where it does not.</summary>
    public static TradeSide Classify(TradePrint trade, Quote? quote)
    {
        ArgumentNullException.ThrowIfNull(trade);

        return trade.Aggressor switch
        {
            AggressorSide.Buy => TradeSide.Buy,
            AggressorSide.Sell => TradeSide.Sell,
            _ => quote is null ? TradeSide.Unknown : QuoteRule(trade.Price, quote.Bid, quote.Ask),
        };
    }

    /// <summary>
    /// The Lee-Ready quote rule: at or through the offer is a buy, at or through the bid a sell, and
    /// anything strictly inside is decided against the mid.
    /// </summary>
    public static TradeSide QuoteRule(double price, double bid, double ask)
    {
        if (!double.IsFinite(price) || bid <= 0d || ask <= 0d || ask < bid) return TradeSide.Unknown;

        if (price >= ask) return TradeSide.Buy;
        if (price <= bid) return TradeSide.Sell;

        var mid = (bid + ask) * 0.5d;
        if (price > mid) return TradeSide.Buy;
        if (price < mid) return TradeSide.Sell;
        return TradeSide.Unknown;
    }

    /// <summary>The tick rule: an uptick is a buy, a downtick a sell, and an unchanged price inherits
    /// the previous classification. Only for feeds with no quote.</summary>
    public static TradeSide TickRule(double price, double previousPrice, TradeSide previousSide)
    {
        if (!double.IsFinite(price) || !double.IsFinite(previousPrice)) return TradeSide.Unknown;
        if (price > previousPrice) return TradeSide.Buy;
        if (price < previousPrice) return TradeSide.Sell;
        return previousSide;
    }
}

/// <summary>
/// Signed traded volume over a rolling window, normalised to [-1, 1].
///
/// <para>The normalisation is what makes this usable. Raw delta is in contracts, so a threshold
/// calibrated on one instrument is meaningless on another and meaningless on the same instrument at a
/// different time of day; the ratio of net to total volume is comparable everywhere, and its extremes
/// mean the same thing in the overnight session as at the open.</para>
///
/// <para><see cref="Cumulative"/> is kept alongside for the classic CVD line, which is a picture
/// rather than a threshold: what a chart reader looks for is divergence between it and price, not its
/// level.</para>
/// </summary>
public sealed class OrderFlowImbalance : IEstimator
{
    private readonly RollingWindow _signed;
    private readonly RollingWindow _total;

    /// <param name="period">How many prints or bars to measure over.</param>
    public OrderFlowImbalance(int period = 100)
    {
        _signed = new RollingWindow(period);
        _total = new RollingWindow(period);
    }

    /// <summary>The window length.</summary>
    public int Period => _signed.Capacity;

    /// <summary>Net signed volume over gross volume, in [-1, 1].</summary>
    public double Value => Num.Clamp(Num.SafeDiv(_signed.Sum, _total.Sum), -1d, 1d);

    /// <inheritdoc/>
    public bool IsReady => _signed.IsFull;

    /// <summary>Signed volume since construction — the cumulative delta line.</summary>
    public double Cumulative { get; private set; }

    /// <summary>Net signed volume inside the window, unnormalised.</summary>
    public double WindowDelta => _signed.Sum;

    /// <summary>Records one classified print.</summary>
    public double Update(double volume, TradeSide side)
    {
        if (!double.IsFinite(volume) || volume <= 0d) return Value;

        var signed = side switch
        {
            TradeSide.Buy => volume,
            TradeSide.Sell => -volume,
            _ => 0d,
        };

        _signed.Update(signed);
        _total.Update(volume);
        Cumulative += signed;
        return Value;
    }

    /// <summary>Records one print, classifying it against a quote when the venue did not.</summary>
    public double Update(TradePrint trade, Quote? quote)
    {
        ArgumentNullException.ThrowIfNull(trade);
        return Update(trade.Size, TradeClassifier.Classify(trade, quote));
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _signed.Reset();
        _total.Reset();
        Cumulative = 0d;
    }
}

/// <summary>
/// Volume-Synchronised Probability of Informed Trading — how one-sided the flow has been, bucketed by
/// volume rather than by time.
///
/// <para>The volume clock is the entire idea. In clock time a quiet hour and a violent minute are the
/// same length of history, so a time-bucketed toxicity measure is dominated by how busy the market
/// happened to be; in volume time each bucket holds the same amount of trading, and a reading is
/// comparable across sessions and instruments.</para>
///
/// <para>Reads in [0, 1]. Sustained high values mean flow that keeps arriving on one side, which is
/// the condition under which a mean-reversion strategy is on the wrong side of somebody who
/// knows something — worth using as a veto rather than as an entry.</para>
/// </summary>
public sealed class Vpin : IEstimator
{
    private readonly RollingWindow _buckets;
    private readonly double _bucketVolume;

    private double _bucketBuy;
    private double _bucketSell;

    /// <param name="bucketVolume">Volume that fills one bucket. Size it so a bucket closes every few
    /// minutes on the instrument being traded.</param>
    /// <param name="buckets">How many completed buckets to average over.</param>
    public Vpin(double bucketVolume, int buckets = 50)
    {
        _bucketVolume = bucketVolume > 0d ? bucketVolume : 1d;
        _buckets = new RollingWindow(buckets);
    }

    /// <summary>Volume that fills one bucket.</summary>
    public double BucketVolume => _bucketVolume;

    /// <summary>Mean one-sidedness across the completed buckets, in [0, 1].</summary>
    public double Value => Num.Clamp(_buckets.Mean, 0d, 1d);

    /// <inheritdoc/>
    public bool IsReady => _buckets.IsFull;

    /// <summary>Completed buckets held.</summary>
    public int BucketCount => _buckets.Count;

    /// <summary>How full the bucket being filled is, in [0, 1].</summary>
    public double BucketProgress => Num.Clamp(Num.SafeDiv(_bucketBuy + _bucketSell, _bucketVolume), 0d, 1d);

    /// <summary>Records one classified print, closing buckets as they fill.</summary>
    public double Update(double volume, TradeSide side)
    {
        if (!double.IsFinite(volume) || volume <= 0d) return Value;

        // Unclassified volume is split rather than dropped: discarding it shrinks the denominator and
        // reports flow as more one-sided than it was, which is the direction that causes a false veto.
        switch (side)
        {
            case TradeSide.Buy: _bucketBuy += volume; break;
            case TradeSide.Sell: _bucketSell += volume; break;
            default:
                _bucketBuy += volume * 0.5d;
                _bucketSell += volume * 0.5d;
                break;
        }

        // While loop, not if: one block print can be several buckets' worth on a thin instrument.
        while (_bucketBuy + _bucketSell >= _bucketVolume)
        {
            var filled = _bucketBuy + _bucketSell;
            _buckets.Update(Num.SafeDiv(Math.Abs(_bucketBuy - _bucketSell), filled));

            // Carry the overflow forward in proportion, so a bucket boundary inside a large print does
            // not throw the remainder away.
            var overflow = filled - _bucketVolume;
            if (overflow <= 0d)
            {
                _bucketBuy = 0d;
                _bucketSell = 0d;
                break;
            }

            var buyShare = Num.SafeDiv(_bucketBuy, filled, 0.5d);
            _bucketBuy = overflow * buyShare;
            _bucketSell = overflow * (1d - buyShare);
        }

        return Value;
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _buckets.Reset();
        _bucketBuy = 0d;
        _bucketSell = 0d;
    }
}

/// <summary>
/// Kyle's lambda: how far price moves per unit of signed volume — the market's depth, measured rather
/// than read off the book.
///
/// <para>A regression of price change on signed volume. What it captures that the visible book does
/// not is the liquidity that is only there when nobody is taking it: a book can look deep and still
/// move on every trade, and lambda notices that where a depth sum does not.</para>
///
/// <para>Rising lambda means the same order now costs more to execute. It belongs on the size of a
/// position, not only on the decision to take one.</para>
/// </summary>
public sealed class KyleLambda : IEstimator
{
    private readonly OnlineRegression _fit;
    private double _previousPrice;
    private double _signedVolume;
    private bool _seeded;

    /// <param name="period">How many intervals to fit over.</param>
    public KyleLambda(int period = 60) => _fit = new OnlineRegression(period);

    /// <summary>The window length in intervals.</summary>
    public int Period => _fit.Period;

    /// <summary>Price impact per unit of signed volume.</summary>
    public double Value => _fit.Slope;

    /// <inheritdoc/>
    public bool IsReady => _fit.IsReady;

    /// <summary>How much of the price variation signed volume explains. A lambda with no fit behind it
    /// is a slope through a cloud.</summary>
    public double RSquared => _fit.RSquared;

    /// <summary>Expected price move from trading <paramref name="signedUnits"/> now.</summary>
    public double ImpactOf(double signedUnits) => Value * signedUnits;

    /// <summary>Accumulates signed volume inside the interval being measured.</summary>
    public void Record(double volume, TradeSide side)
    {
        if (!double.IsFinite(volume) || volume <= 0d) return;

        _signedVolume += side switch
        {
            TradeSide.Buy => volume,
            TradeSide.Sell => -volume,
            _ => 0d,
        };
    }

    /// <summary>Closes the interval at <paramref name="price"/>, folding one observation into the fit
    /// and starting the next. Call it per bar, or on a fixed volume or time slice.</summary>
    public double Close(double price)
    {
        if (!double.IsFinite(price)) return Value;

        if (_seeded) _fit.Update(_signedVolume, price - _previousPrice);

        _previousPrice = price;
        _signedVolume = 0d;
        _seeded = true;
        return Value;
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _fit.Reset();
        _previousPrice = 0d;
        _signedVolume = 0d;
        _seeded = false;
    }
}
