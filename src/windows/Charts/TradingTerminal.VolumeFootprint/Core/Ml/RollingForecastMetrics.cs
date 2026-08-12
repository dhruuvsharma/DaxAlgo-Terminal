namespace TradingTerminal.Core.Ml;

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
