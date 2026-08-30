using TradingTerminal.Core.Domain;

namespace DaxAlgo.Sdk.Quant;

/// <summary>
/// Wilder's Relative Strength Index.
///
/// <para>Two things here are worth more than the formula. The averages are <see cref="Wilder"/> and
/// not <see cref="Ema"/> — an RSI smoothed the other way is a different oscillator that crosses 30
/// and 70 at different times. And gains and losses are averaged <b>over every bar</b>, including the
/// bars where one side was zero; averaging only the up-moves over the up-bars is the common shortcut
/// and it holds the index too high through a downtrend.</para>
/// </summary>
public sealed class Rsi : IEstimator
{
    private readonly Wilder _gains;
    private readonly Wilder _losses;
    private double _previous;
    private bool _seeded;

    /// <param name="period">Wilder's period; 14 classically.</param>
    public Rsi(int period = 14)
    {
        _gains = new Wilder(period);
        _losses = new Wilder(period);
    }

    /// <summary>The period this index was constructed with.</summary>
    public int Period => _gains.Period;

    /// <summary>The index, 0 to 100. Fifty until prices arrive, because that is the neutral reading
    /// rather than a false extreme at one end.</summary>
    public double Value { get; private set; } = 50d;

    /// <inheritdoc/>
    public bool IsReady => _gains.IsReady;

    /// <summary>Folds in one price and returns the index.</summary>
    public double Update(double price)
    {
        if (!double.IsFinite(price)) return Value;

        if (!_seeded)
        {
            _previous = price;
            _seeded = true;
            return Value;
        }

        var change = price - _previous;
        _previous = price;

        var averageGain = _gains.Update(Math.Max(0d, change));
        var averageLoss = _losses.Update(Math.Max(0d, -change));

        // A pure run of gains has no loss to divide by. A hundred is the limit the formula approaches
        // and the reading a trader expects to see, where a guarded zero would read as the opposite.
        Value = averageLoss < Num.Epsilon
            ? averageGain < Num.Epsilon ? 50d : 100d
            : 100d - (100d / (1d + (averageGain / averageLoss)));

        return Value;
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _gains.Reset();
        _losses.Reset();
        _previous = 0d;
        _seeded = false;
        Value = 50d;
    }
}

/// <summary>
/// True range and Wilder's Average True Range — the volatility unit stops and targets should be
/// written in.
///
/// <para>True range is the bar's own span <b>or</b> the gap from the previous close, whichever is
/// larger, which is what makes it survive an overnight gap that a high-minus-low misses entirely. An
/// ATR-sized stop is the same stop on every instrument; a stop of "twenty ticks" is a different stop
/// on each one, and that difference is why a threshold ported between symbols stops working.</para>
/// </summary>
public sealed class Atr : IEstimator
{
    private readonly Wilder _average;
    private double _previousClose;
    private bool _seeded;

    /// <param name="period">Wilder's period; 14 classically.</param>
    public Atr(int period = 14) => _average = new Wilder(period);

    /// <summary>The period this average was constructed with.</summary>
    public int Period => _average.Period;

    /// <inheritdoc/>
    public double Value => _average.Value;

    /// <inheritdoc/>
    public bool IsReady => _average.IsReady;

    /// <summary>The most recent bar's true range, before smoothing.</summary>
    public double LastTrueRange { get; private set; }

    /// <summary>Folds in one bar and returns the average true range.</summary>
    public double Update(OhlcvBar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        return Update(bar.High, bar.Low, bar.Close);
    }

    /// <summary>Folds in one bar's high, low and close.</summary>
    public double Update(double high, double low, double close)
    {
        if (!double.IsFinite(high) || !double.IsFinite(low) || !double.IsFinite(close)) return Value;

        LastTrueRange = _seeded ? TrueRange(high, low, _previousClose) : high - low;
        _previousClose = close;
        _seeded = true;
        return _average.Update(LastTrueRange);
    }

    /// <summary>One bar's true range against the previous close.</summary>
    public static double TrueRange(double high, double low, double previousClose) =>
        Math.Max(high - low, Math.Max(Math.Abs(high - previousClose), Math.Abs(low - previousClose)));

    /// <inheritdoc/>
    public void Reset()
    {
        _average.Reset();
        _previousClose = 0d;
        _seeded = false;
        LastTrueRange = 0d;
    }
}

/// <summary>
/// Moving Average Convergence Divergence: a fast average minus a slow one, and an average over the
/// difference.
///
/// <para>The <see cref="Histogram"/> is what is normally traded, and what
/// <c>DaxAlgo.Sdk.Drawing.Histogram</c> exists to draw — a signed quantity around zero where the sign
/// change is the event and the magnitude is the conviction.</para>
/// </summary>
public sealed class Macd : IEstimator
{
    private readonly Ema _fast;
    private readonly Ema _slow;
    private readonly Ema _signal;

    /// <param name="fastPeriod">The fast span; 12 classically.</param>
    /// <param name="slowPeriod">The slow span; 26 classically.</param>
    /// <param name="signalPeriod">The span of the average over the difference; 9 classically.</param>
    public Macd(int fastPeriod = 12, int slowPeriod = 26, int signalPeriod = 9)
    {
        _fast = new Ema(fastPeriod);
        _slow = new Ema(slowPeriod);
        _signal = new Ema(signalPeriod);
    }

    /// <summary>Fast minus slow.</summary>
    public double Line { get; private set; }

    /// <summary>The average of <see cref="Line"/>.</summary>
    public double Signal => _signal.Value;

    /// <summary><see cref="Line"/> minus <see cref="Signal"/> — one bar of the histogram.</summary>
    public double Histogram { get; private set; }

    /// <summary>The histogram, so this reads like the other estimators.</summary>
    public double Value => Histogram;

    /// <inheritdoc/>
    public bool IsReady => _slow.IsReady && _signal.IsReady;

    /// <summary>Folds in one price and returns the histogram.</summary>
    public double Update(double price)
    {
        var fast = _fast.Update(price);
        var slow = _slow.Update(price);
        Line = fast - slow;
        Histogram = Line - _signal.Update(Line);
        return Histogram;
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _fast.Reset();
        _slow.Reset();
        _signal.Reset();
        Line = 0d;
        Histogram = 0d;
    }
}

/// <summary>
/// Bollinger bands: a moving average with a volatility-scaled envelope.
///
/// <para><see cref="PercentB"/> and <see cref="Width"/> are the members worth reaching for. Position
/// within the band and the band's own width are comparable across instruments and across regimes;
/// the raw band prices are not, and a rule written against them is a rule about one symbol on one
/// day.</para>
/// </summary>
public sealed class BollingerBands : IEstimator
{
    private readonly RollingWindow _window;
    private readonly double _deviations;

    /// <param name="period">The window length; 20 classically.</param>
    /// <param name="deviations">Half-width in standard deviations; 2 classically.</param>
    public BollingerBands(int period = 20, double deviations = 2d)
    {
        _window = new RollingWindow(period);
        _deviations = deviations > 0d ? deviations : 2d;
    }

    /// <summary>The moving average at the centre of the envelope.</summary>
    public double Middle => _window.Mean;

    /// <summary>The upper band.</summary>
    public double Upper => Middle + (_deviations * _window.StandardDeviation);

    /// <summary>The lower band.</summary>
    public double Lower => Middle - (_deviations * _window.StandardDeviation);

    /// <summary>Band width as a fraction of the middle — the squeeze and expansion measure, comparable
    /// across instruments in a way an absolute width is not.</summary>
    public double Width => Num.SafeDiv(Upper - Lower, Middle);

    /// <summary>Where the last price sat in the envelope: 0 at the lower band, 1 at the upper, and
    /// outside [0, 1] beyond them.</summary>
    public double PercentB { get; private set; } = 0.5d;

    /// <summary><see cref="PercentB"/>, so this reads like the other estimators.</summary>
    public double Value => PercentB;

    /// <inheritdoc/>
    public bool IsReady => _window.IsFull;

    /// <summary>Adds one price and returns its position within the envelope.</summary>
    public double Update(double price)
    {
        _window.Update(price);
        PercentB = Num.SafeDiv(price - Lower, Upper - Lower, 0.5d);
        return PercentB;
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _window.Reset();
        PercentB = 0.5d;
    }
}

/// <summary>
/// Volume-weighted average price, and the volume-weighted dispersion around it.
///
/// <para><see cref="Reset"/> at the session open is not optional. A VWAP carried across sessions
/// anchors to yesterday's volume and drifts further from anything tradeable with every bar — so this
/// type makes resetting an explicit act rather than hiding a calendar inside itself. The SDK has no
/// clock, and a session boundary is the host's business.</para>
///
/// <para><see cref="Band"/> gives the standard-deviation envelope that makes VWAP a mean-reversion
/// reference rather than just a line.</para>
/// </summary>
public sealed class Vwap : IEstimator
{
    private double _notional;
    private double _volume;
    private double _notionalSquared;

    /// <summary>The volume-weighted average price, or zero before any volume.</summary>
    public double Value => Num.SafeDiv(_notional, _volume);

    /// <summary>True once any volume has been recorded.</summary>
    public bool IsReady => _volume > 0d;

    /// <summary>Volume accumulated since the last reset.</summary>
    public double Volume => _volume;

    /// <summary>The volume-weighted standard deviation of price around <see cref="Value"/>.</summary>
    public double Deviation
    {
        get
        {
            var mean = Value;
            var variance = Num.SafeDiv(_notionalSquared, _volume) - (mean * mean);
            return Math.Sqrt(Math.Max(0d, variance));
        }
    }

    /// <summary>The VWAP shifted by <paramref name="deviations"/> volume-weighted standard deviations.</summary>
    public double Band(double deviations) => Value + (deviations * Deviation);

    /// <summary>Adds one trade or bar and returns the new VWAP.</summary>
    public double Update(double price, double volume)
    {
        if (!double.IsFinite(price) || !double.IsFinite(volume) || volume <= 0d) return Value;

        _notional += price * volume;
        _notionalSquared += price * price * volume;
        _volume += volume;
        return Value;
    }

    /// <summary>Adds a bar at its typical price — <c>(H + L + C) / 3</c>, the conventional
    /// single-price stand-in for a bar's traded distribution.</summary>
    public double Update(OhlcvBar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        return Update((bar.High + bar.Low + bar.Close) / 3d, bar.Volume);
    }

    /// <summary>Starts a new session. Call it on the session boundary the host reports.</summary>
    public void Reset()
    {
        _notional = 0d;
        _volume = 0d;
        _notionalSquared = 0d;
    }
}

/// <summary>
/// Realised volatility: the root mean square of the log returns in a window.
///
/// <para>Measured about zero rather than about the sample mean, which is deliberate — a drift
/// estimated from a short window is noise, and subtracting it as though it were signal understates
/// the volatility exactly when it matters.</para>
///
/// <para>Annualisation is a separate call rather than a constructor flag, because the factor depends
/// on what one sample is: 252 for daily bars, 98 280 for one-minute bars on a 6.5-hour session. A
/// wrong factor produces a number that looks like a percentage and is out by an order of magnitude,
/// so <see cref="Value"/> stays the per-sample figure, which is always well defined.</para>
/// </summary>
public sealed class RealizedVolatility : IEstimator
{
    private readonly RollingWindow _returns;
    private double _previousPrice;
    private bool _seeded;

    /// <param name="period">How many returns to keep.</param>
    public RealizedVolatility(int period = 60) => _returns = new RollingWindow(period);

    /// <summary>The window length in returns.</summary>
    public int Period => _returns.Capacity;

    /// <summary>Volatility per sample.</summary>
    public double Value { get; private set; }

    /// <inheritdoc/>
    public bool IsReady => _returns.IsFull;

    /// <summary>The most recent log return.</summary>
    public double LastReturn { get; private set; }

    /// <summary>Adds a price and returns the per-sample volatility.</summary>
    public double Update(double price)
    {
        if (!double.IsFinite(price) || price <= 0d) return Value;

        if (!_seeded)
        {
            _previousPrice = price;
            _seeded = true;
            return Value;
        }

        LastReturn = Num.LogReturn(price, _previousPrice);
        _previousPrice = price;
        _returns.Update(LastReturn);

        var sumSquares = 0d;
        for (var i = 0; i < _returns.Count; i++) sumSquares += _returns[i] * _returns[i];
        Value = Math.Sqrt(Num.SafeDiv(sumSquares, _returns.Count));
        return Value;
    }

    /// <summary>The volatility scaled to a longer horizon by the square root of
    /// <paramref name="samplesPerPeriod"/> — 252 for daily samples, to get an annual figure.</summary>
    public double Annualized(double samplesPerPeriod) =>
        samplesPerPeriod > 0d ? Value * Math.Sqrt(samplesPerPeriod) : Value;

    /// <inheritdoc/>
    public void Reset()
    {
        _returns.Reset();
        _previousPrice = 0d;
        _seeded = false;
        Value = 0d;
        LastReturn = 0d;
    }
}
