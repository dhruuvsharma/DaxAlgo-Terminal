namespace TradingTerminal.Core.Ml;

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
