namespace DaxAlgo.Sdk.Quant;

/// <summary>
/// A one-dimensional Kalman filter over a level that drifts — the adaptive alternative to a moving
/// average.
///
/// <para>What it buys over an EMA is that the smoothing is not fixed. The gain is derived from the
/// ratio of process noise to measurement noise, so the estimate follows a genuine move quickly and
/// ignores a noisy tick, where an EMA of any single period must choose one behaviour and keep it
/// through both.</para>
///
/// <para>Only the ratio of the two noises matters, not their absolute size, which is what makes this
/// tunable in practice: raise <see cref="ProcessNoise"/> to track faster, raise
/// <see cref="MeasurementNoise"/> to smooth harder.</para>
/// </summary>
public sealed class KalmanLevel : IEstimator
{
    private double _errorCovariance = 1d;
    private int _count;

    /// <param name="processNoise">How much the true level is expected to move between samples.</param>
    /// <param name="measurementNoise">How noisy each observation is.</param>
    public KalmanLevel(double processNoise = 1e-5d, double measurementNoise = 1e-2d)
    {
        ProcessNoise = processNoise > 0d ? processNoise : 1e-5d;
        MeasurementNoise = measurementNoise > 0d ? measurementNoise : 1e-2d;
    }

    /// <summary>Expected movement of the true level between samples.</summary>
    public double ProcessNoise { get; }

    /// <summary>Expected noise in each observation.</summary>
    public double MeasurementNoise { get; }

    /// <summary>The filtered level.</summary>
    public double Value { get; private set; }

    /// <summary>The gain last applied, in [0, 1]: near one the filter is tracking, near zero it is
    /// smoothing. Worth surfacing on a chart — it says which of the two the filter thinks it is
    /// doing.</summary>
    public double Gain { get; private set; }

    /// <summary>The last observation minus the prediction that preceded it. A run of same-signed
    /// innovations means the model is behind the market, not that the market is surprising.</summary>
    public double Innovation { get; private set; }

    /// <inheritdoc/>
    public bool IsReady => _count > 1;

    /// <summary>Folds in one observation and returns the filtered level.</summary>
    public double Update(double measurement)
    {
        if (!double.IsFinite(measurement)) return Value;

        if (_count == 0)
        {
            Value = measurement;
            _count = 1;
            return Value;
        }

        // Predict: the level is a random walk, so the estimate is unchanged and only its uncertainty
        // grows.
        _errorCovariance += ProcessNoise;

        // Correct.
        Gain = Num.SafeDiv(_errorCovariance, _errorCovariance + MeasurementNoise);
        Innovation = measurement - Value;
        Value += Gain * Innovation;
        _errorCovariance *= 1d - Gain;

        _count++;
        return Value;
    }

    /// <inheritdoc/>
    public void Reset()
    {
        Value = 0d;
        Gain = 0d;
        Innovation = 0d;
        _errorCovariance = 1d;
        _count = 0;
    }
}

/// <summary>
/// A two-state Kalman filter that tracks a hedge ratio and an intercept as they drift — the pairs
/// trading estimator.
///
/// <para>The difference from <see cref="OnlineRegression"/> is what happens at the window edge. A
/// rolling regression forgets its oldest observation abruptly, so the hedge ratio steps whenever an
/// influential point leaves the window, and the spread steps with it — through the thresholds the
/// strategy is watching. This one has no window and no edge: every observation decays smoothly, so
/// the ratio moves when the relationship moves and not when the buffer does.</para>
///
/// <para><see cref="Spread"/> is the residual after hedging, and the number a z-score should be taken
/// of.</para>
/// </summary>
public sealed class KalmanHedgeRatio
{
    // 2x2 state covariance, held as four scalars: a matrix library for one fixed size would be a
    // dependency and an allocation per update for arithmetic that fits in a screen.
    private double _p00 = 1d;
    private double _p01;
    private double _p10;
    private double _p11 = 1d;

    private int _count;

    /// <param name="processNoise">How fast the hedge ratio is allowed to drift.</param>
    /// <param name="measurementNoise">How noisy the observed relationship is.</param>
    public KalmanHedgeRatio(double processNoise = 1e-5d, double measurementNoise = 1e-3d)
    {
        ProcessNoise = processNoise > 0d ? processNoise : 1e-5d;
        MeasurementNoise = measurementNoise > 0d ? measurementNoise : 1e-3d;
    }

    /// <summary>How fast the ratio may drift between samples.</summary>
    public double ProcessNoise { get; }

    /// <summary>Expected noise in the observed relationship.</summary>
    public double MeasurementNoise { get; }

    /// <summary>The current hedge ratio — how many units of x hedge one unit of y.</summary>
    public double HedgeRatio { get; private set; }

    /// <summary>The current intercept.</summary>
    public double Intercept { get; private set; }

    /// <summary>The last residual: <c>y − (ratio·x + intercept)</c>. The tradeable spread.</summary>
    public double Spread { get; private set; }

    /// <summary>True once two observations have been folded in.</summary>
    public bool IsReady => _count > 1;

    /// <summary>Folds in one observed pair and returns the residual spread.</summary>
    public double Update(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return Spread;

        if (_count == 0)
        {
            // Seed on the first pair: ratio one, intercept whatever reconciles them. A zero seed makes
            // the first several spreads the size of the price rather than the size of a residual, and
            // a strategy watching for an extreme spread sees one immediately.
            HedgeRatio = 1d;
            Intercept = y - x;
            Spread = 0d;
            _count = 1;
            return Spread;
        }

        // Predict: the state is a random walk, so only its uncertainty grows, on the diagonal.
        _p00 += ProcessNoise;
        _p11 += ProcessNoise;

        // Observation row is [x, 1] — y is ratio*x + intercept plus noise.
        var predicted = (HedgeRatio * x) + Intercept;
        Spread = y - predicted;

        var px0 = (_p00 * x) + _p01;
        var px1 = (_p10 * x) + _p11;
        var innovationVariance = (x * px0) + px1 + MeasurementNoise;

        var k0 = Num.SafeDiv(px0, innovationVariance);
        var k1 = Num.SafeDiv(px1, innovationVariance);

        HedgeRatio += k0 * Spread;
        Intercept += k1 * Spread;

        // P = (I - K H) P, with H = [x, 1].
        var p00 = _p00 - (k0 * px0);
        var p01 = _p01 - (k0 * px1);
        var p10 = _p10 - (k1 * px0);
        var p11 = _p11 - (k1 * px1);
        _p00 = p00;
        _p01 = p01;
        _p10 = p10;
        _p11 = p11;

        _count++;
        return Spread;
    }

    /// <summary>Returns the filter to its unseeded state.</summary>
    public void Reset()
    {
        HedgeRatio = 0d;
        Intercept = 0d;
        Spread = 0d;
        _p00 = 1d;
        _p01 = 0d;
        _p10 = 0d;
        _p11 = 1d;
        _count = 0;
    }
}
