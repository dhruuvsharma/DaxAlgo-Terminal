namespace DaxAlgo.Sdk.Quant;

/// <summary>Rolling accuracy read-out for one one-step-ahead forecaster (a model, or the baseline it
/// is being compared against). <c>MeanAbsoluteError</c> and <c>DirectionalHitRate</c> are over the
/// rolling window; <c>ScoredCount</c> is the lifetime number of scored forecasts.</summary>
public readonly record struct ForecastAccuracy(
    double MeanAbsoluteError,
    double DirectionalHitRate,
    long ScoredCount);

/// <summary>Rolling calibration read-out for one event forecaster: mean squared probability error
/// (<c>Brier</c>, lower is better; 0.25 = always saying 50%), the event's observed <c>BaseRate</c>
/// over the same window — the climatology comparison, since a useful model beats
/// <c>baseRate·(1−baseRate)</c> — and the lifetime scored count.</summary>
public readonly record struct EventScore(double Brier, double BaseRate, long ScoredCount);


/// <summary>
/// Fixed-window rolling MAE (in ticks) and directional hit-rate for 1-step-ahead POC forecasts.
/// A hit means the predicted and realized moves agree in sign; a zero realized move counts as a
/// hit only when the prediction was smaller than half a tick (i.e. the model also called "flat").
/// Ring-buffered, O(1) memory, deterministic.
/// </summary>
public sealed class RollingForecastMetrics
{
    private const double FlatToleranceTicks = 0.5;

    private readonly double[] _absoluteErrors;
    private readonly bool[] _hits;
    private int _next;
    private int _count;
    private long _totalScored;

    public RollingForecastMetrics(int window = 100)
    {
        if (window <= 0) throw new ArgumentOutOfRangeException(nameof(window));
        _absoluteErrors = new double[window];
        _hits = new bool[window];
    }

    /// <summary>Scores one realized forecast, both in ticks relative to the reference bar's POC.
    /// Non-finite inputs are ignored (an unusable forecast is not evidence either way).</summary>
    public void Score(double predictedDeltaTicks, double realizedDeltaTicks)
    {
        if (!double.IsFinite(predictedDeltaTicks) || !double.IsFinite(realizedDeltaTicks)) return;

        var hit = realizedDeltaTicks == 0
            ? Math.Abs(predictedDeltaTicks) < FlatToleranceTicks
            : Math.Sign(predictedDeltaTicks) == Math.Sign(realizedDeltaTicks);

        _absoluteErrors[_next] = Math.Abs(predictedDeltaTicks - realizedDeltaTicks);
        _hits[_next] = hit;
        _next = (_next + 1) % _absoluteErrors.Length;
        if (_count < _absoluteErrors.Length) _count++;
        _totalScored++;
    }

    public ForecastAccuracy Snapshot()
    {
        if (_count == 0) return new ForecastAccuracy(double.NaN, double.NaN, 0);

        double errorSum = 0;
        var hitCount = 0;
        for (var i = 0; i < _count; i++)
        {
            errorSum += _absoluteErrors[i];
            if (_hits[i]) hitCount++;
        }
        return new ForecastAccuracy(errorSum / _count, (double)hitCount / _count, _totalScored);
    }

    public void Reset()
    {
        _next = 0;
        _count = 0;
        _totalScored = 0;
    }
}

/// <summary>
/// Fixed-window rolling Brier score for a binary-event probability forecaster: mean of
/// <c>(p − y)²</c> over the last N scored forecasts, alongside the event's observed base rate over
/// the same window so the read-out can be compared against "climatology" (always predicting the
/// base rate scores <c>r(1−r)</c>). Ring-buffered, O(1) memory, deterministic. The sibling of
/// <see cref="RollingForecastMetrics"/> for probability targets.
/// </summary>
public sealed class RollingBrierScore
{
    private readonly double[] _squaredErrors;
    private readonly bool[] _outcomes;
    private int _next;
    private int _count;
    private long _totalScored;

    public RollingBrierScore(int window = 200)
    {
        if (window <= 0) throw new ArgumentOutOfRangeException(nameof(window));
        _squaredErrors = new double[window];
        _outcomes = new bool[window];
    }

    /// <summary>Scores one realized event forecast. Non-finite probabilities are ignored;
    /// finite ones are clamped to [0, 1] before scoring.</summary>
    public void Score(double probability, bool occurred)
    {
        if (!double.IsFinite(probability)) return;
        var p = Math.Clamp(probability, 0.0, 1.0);
        var y = occurred ? 1.0 : 0.0;

        _squaredErrors[_next] = (p - y) * (p - y);
        _outcomes[_next] = occurred;
        _next = (_next + 1) % _squaredErrors.Length;
        if (_count < _squaredErrors.Length) _count++;
        _totalScored++;
    }

    public EventScore Snapshot()
    {
        if (_count == 0) return new EventScore(double.NaN, double.NaN, 0);

        double errorSum = 0;
        var occurredCount = 0;
        for (var i = 0; i < _count; i++)
        {
            errorSum += _squaredErrors[i];
            if (_outcomes[i]) occurredCount++;
        }
        return new EventScore(errorSum / _count, (double)occurredCount / _count, _totalScored);
    }

    public void Reset()
    {
        _next = 0;
        _count = 0;
        _totalScored = 0;
    }
}

