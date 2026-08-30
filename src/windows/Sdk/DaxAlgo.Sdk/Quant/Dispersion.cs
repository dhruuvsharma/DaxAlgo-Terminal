namespace DaxAlgo.Sdk.Quant;

/// <summary>
/// Welford's running mean and variance — the whole history, in constant space and one pass.
///
/// <para>Use it where a <see cref="RollingWindow"/> would be wrong: the statistic is about everything
/// seen since the session began rather than the last N samples, and there is no length to choose. The
/// update is algebraically identical to the naive sum-of-squares form and numerically nothing like it
/// — subtracting two large nearly-equal sums is what makes the textbook version return negative
/// variances on price data, and it does that quietly.</para>
/// </summary>
public sealed class Welford : IEstimator
{
    private long _count;
    private double _mean;
    private double _sumSquares;

    /// <summary>How many samples have been folded in.</summary>
    public long Count => _count;

    /// <summary>The running mean.</summary>
    public double Mean => _mean;

    /// <summary>The sample variance (Bessel-corrected), or zero below two samples.</summary>
    public double Variance => _count > 1 ? _sumSquares / (_count - 1) : 0d;

    /// <summary>The sample standard deviation.</summary>
    public double StandardDeviation => Math.Sqrt(Variance);

    /// <summary>The mean, so <see cref="Welford"/> reads like every other estimator here.</summary>
    public double Value => _mean;

    /// <summary>True from two samples, which is the first point at which a variance exists.</summary>
    public bool IsReady => _count > 1;

    /// <summary>Folds in one sample and returns the running mean.</summary>
    public double Update(double sample)
    {
        if (!double.IsFinite(sample)) return _mean;

        _count++;
        var delta = sample - _mean;
        _mean += delta / _count;
        _sumSquares += delta * (sample - _mean);
        return _mean;
    }

    /// <summary>How many standard deviations <paramref name="value"/> sits from the running mean.</summary>
    public double ZScoreOf(double value) => Num.ZScore(value, _mean, StandardDeviation);

    /// <inheritdoc/>
    public void Reset()
    {
        _count = 0;
        _mean = 0d;
        _sumSquares = 0d;
    }
}

/// <summary>
/// Exponentially weighted mean and variance — Welford's statistic for a series whose distribution
/// moves.
///
/// <para>The right choice when the question is "is this move large *for now*" rather than "for the
/// session". A volatility estimate that weights this morning's calm equally with the last five
/// minutes will call every afternoon move extreme; this one forgets at a rate you set. <c>λ = 0.94</c>
/// is RiskMetrics' daily decay and a defensible default; smaller forgets faster.</para>
/// </summary>
public sealed class EwmaVariance : IEstimator
{
    private readonly double _lambda;
    private double _mean;
    private double _variance;
    private int _count;

    /// <param name="lambda">Decay in (0, 1). Clamped away from both ends, since 0 keeps nothing and 1
    /// never updates.</param>
    public EwmaVariance(double lambda = 0.94d) => _lambda = Num.Clamp(lambda, 0.01d, 0.999d);

    /// <summary>The decay this estimator was constructed with.</summary>
    public double Lambda => _lambda;

    /// <summary>The exponentially weighted mean.</summary>
    public double Mean => _mean;

    /// <summary>The exponentially weighted variance.</summary>
    public double Variance => _variance;

    /// <summary>The exponentially weighted standard deviation — the one a threshold is usually built on.</summary>
    public double StandardDeviation => Math.Sqrt(Math.Max(0d, _variance));

    /// <summary>The standard deviation, so this reads like the other estimators.</summary>
    public double Value => StandardDeviation;

    /// <summary>True from two samples.</summary>
    public bool IsReady => _count > 1;

    /// <summary>Folds in one sample and returns the current standard deviation.</summary>
    public double Update(double sample)
    {
        if (!double.IsFinite(sample)) return StandardDeviation;

        if (_count == 0)
        {
            _mean = sample;
            _count = 1;
            return 0d;
        }

        var deviation = sample - _mean;
        _mean += (1d - _lambda) * deviation;
        _variance = (_lambda * _variance) + ((1d - _lambda) * deviation * deviation);
        _count++;
        return StandardDeviation;
    }

    /// <summary>How many current standard deviations <paramref name="value"/> sits from the current mean.</summary>
    public double ZScoreOf(double value) => Num.ZScore(value, _mean, StandardDeviation);

    /// <inheritdoc/>
    public void Reset()
    {
        _mean = 0d;
        _variance = 0d;
        _count = 0;
    }
}

/// <summary>
/// A windowed z-score with an explicit warm-up, because the number is worthless before it converges.
///
/// <para>A z-score over eight samples is not a small z-score, it is a different quantity: with that
/// little data the extreme values are the sample, so the statistic reads near zero exactly when the
/// series is most unusual. <see cref="MinimumSamples"/> defaults to thirty for that reason and gates
/// <see cref="IsReady"/> independently of whether the window has filled, so a strategy asking for a
/// 500-sample window still gets an honest answer about when it may act.</para>
/// </summary>
public sealed class ZScore : IEstimator
{
    private readonly RollingWindow _window;

    /// <param name="period">The window length.</param>
    /// <param name="minimumSamples">Samples required before <see cref="IsReady"/>. Defaults to the
    /// smaller of thirty and the window length.</param>
    public ZScore(int period, int minimumSamples = 0)
    {
        _window = new RollingWindow(period);
        MinimumSamples = minimumSamples > 0
            ? Math.Min(minimumSamples, _window.Capacity)
            : Math.Min(30, _window.Capacity);
    }

    /// <summary>The window length.</summary>
    public int Period => _window.Capacity;

    /// <summary>How many samples must arrive before the score is trusted.</summary>
    public int MinimumSamples { get; }

    /// <summary>The score of the most recent sample.</summary>
    public double Value { get; private set; }

    /// <inheritdoc/>
    public bool IsReady => _window.Count >= MinimumSamples;

    /// <summary>The window's mean.</summary>
    public double Mean => _window.Mean;

    /// <summary>The window's standard deviation.</summary>
    public double StandardDeviation => _window.StandardDeviation;

    /// <summary>Adds a sample and returns its score against the window it just joined.</summary>
    public double Update(double sample)
    {
        _window.Update(sample);
        Value = _window.ZScoreOf(sample);
        return Value;
    }

    /// <inheritdoc/>
    public void Reset()
    {
        _window.Reset();
        Value = 0d;
    }
}
