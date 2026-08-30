namespace DaxAlgo.Sdk.Quant;

/// <summary>
/// What every streaming estimator in this namespace has in common: a current value, and whether it
/// means anything yet.
///
/// <para><see cref="IsReady"/> is the member that matters. An estimator's first few samples are not a
/// small version of its converged value, they are noise with the same type — and a strategy that
/// trades a 200-period z-score on its third tick is not trading a z-score. Every implementation here
/// gates on its own warm-up so an author can write <c>if (!indicator.IsReady) return;</c> once
/// instead of tracking sample counts by hand.</para>
/// </summary>
public interface IEstimator
{
    /// <summary>The current estimate. Meaningless until <see cref="IsReady"/>.</summary>
    double Value { get; }

    /// <summary>True once enough samples have arrived for <see cref="Value"/> to mean something.</summary>
    bool IsReady { get; }

    /// <summary>Returns the estimator to its unseeded state — a new session, or a new instrument.</summary>
    void Reset();
}

/// <summary>
/// Exponential moving average, seeded with its first sample.
///
/// <para>Seeding is the whole reason this type exists rather than one line in a strategy. Starting
/// from zero and letting the recursion converge biases the entire series toward zero for several
/// multiples of the period, which on a price series means an average that is wrong by the price
/// itself. It is the most common error in generated indicator code and it is invisible: the curve
/// looks plausible, just late and low.</para>
/// </summary>
public sealed class Ema : IEstimator
{
    private readonly double _alpha;
    private double _value;
    private int _count;

    /// <param name="period">The span, in samples. Two or more; anything smaller is a passthrough.</param>
    public Ema(int period)
    {
        Period = Math.Max(1, period);
        _alpha = 2d / (Period + 1d);
    }

    /// <summary>The span this average was constructed with.</summary>
    public int Period { get; }

    /// <inheritdoc/>
    public double Value => _value;

    /// <summary>True once <see cref="Period"/> samples have arrived. The average holds a usable number
    /// from the first one — it is seeded — but it has not yet forgotten that sample.</summary>
    public bool IsReady => _count >= Period;

    /// <summary>How many samples have been seen.</summary>
    public int Count => _count;

    /// <summary>Folds in one sample and returns the new average. Non-finite input is ignored rather
    /// than allowed to poison the recursion permanently.</summary>
    public double Update(double sample)
    {
        if (!double.IsFinite(sample)) return _value;

        if (_count == 0) _value = sample;
        else _value += _alpha * (sample - _value);

        _count++;
        return _value;
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _value = 0d;
        _count = 0;
    }
}

/// <summary>
/// Wilder's smoothing — <c>α = 1/period</c>, not <c>2/(period+1)</c>.
///
/// <para>This is a different average from <see cref="Ema"/> and it is not interchangeable with it. RSI,
/// ATR, ADX and DMI are all defined in terms of this one; smoothing them with an EMA of the same
/// period produces a curve roughly twice as fast, which crosses its thresholds at different times and
/// therefore trades differently. Nothing downstream can detect the substitution — the result compiles,
/// runs and draws — so it exists as its own type to make the choice explicit at the call site.</para>
/// </summary>
public sealed class Wilder : IEstimator
{
    private readonly double _alpha;
    private double _value;
    private int _count;

    /// <param name="period">Wilder's period — 14 in every classical definition.</param>
    public Wilder(int period)
    {
        Period = Math.Max(1, period);
        _alpha = 1d / Period;
    }

    /// <summary>The period this smoother was constructed with.</summary>
    public int Period { get; }

    /// <inheritdoc/>
    public double Value => _value;

    /// <inheritdoc/>
    public bool IsReady => _count >= Period;

    /// <summary>How many samples have been seen.</summary>
    public int Count => _count;

    /// <summary>Folds in one sample and returns the new average.</summary>
    public double Update(double sample)
    {
        if (!double.IsFinite(sample)) return _value;

        if (_count == 0) _value = sample;
        else _value += _alpha * (sample - _value);

        _count++;
        return _value;
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _value = 0d;
        _count = 0;
    }
}

/// <summary>
/// Simple moving average over a fixed window.
///
/// <para>Backed by <see cref="RollingWindow"/>, so the sum is maintained at the edges rather than
/// re-added every sample, and the window's other statistics are available from the same buffer if the
/// strategy also wants dispersion.</para>
/// </summary>
public sealed class Sma : IEstimator
{
    private readonly RollingWindow _window;

    /// <param name="period">Window length in samples.</param>
    public Sma(int period) => _window = new RollingWindow(period);

    /// <summary>The window length.</summary>
    public int Period => _window.Capacity;

    /// <inheritdoc/>
    public double Value => _window.Mean;

    /// <summary>True once the window is full. A mean over three of a hundred requested samples is a
    /// mean over three samples, and treating it as the requested one is how a strategy trades noise
    /// for its first minute of every session.</summary>
    public bool IsReady => _window.IsFull;

    /// <summary>The samples currently in the window.</summary>
    public int Count => _window.Count;

    /// <summary>Adds one sample and returns the new mean.</summary>
    public double Update(double sample)
    {
        _window.Update(sample);
        return _window.Mean;
    }

    /// <inheritdoc/>
    public void Reset() => _window.Reset();
}

/// <summary>
/// A double exponential smoother, so a fast average can be de-lagged without shortening its period.
///
/// <para><c>2·EMA(x) − EMA(EMA(x))</c>. Useful where the lag of a plain average is the problem and
/// shortening the period would only add noise; it overshoots turns in exchange, which is the trade
/// being made and worth knowing about before it is used as a trigger.</para>
/// </summary>
public sealed class Dema : IEstimator
{
    private readonly Ema _first;
    private readonly Ema _second;

    /// <param name="period">The span of both stages.</param>
    public Dema(int period)
    {
        _first = new Ema(period);
        _second = new Ema(period);
    }

    /// <summary>The span this smoother was constructed with.</summary>
    public int Period => _first.Period;

    /// <inheritdoc/>
    public double Value { get; private set; }

    /// <inheritdoc/>
    public bool IsReady => _second.IsReady;

    /// <summary>Folds in one sample and returns the de-lagged average.</summary>
    public double Update(double sample)
    {
        var first = _first.Update(sample);
        var second = _second.Update(first);
        Value = (2d * first) - second;
        return Value;
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _first.Reset();
        _second.Reset();
        Value = 0d;
    }
}
