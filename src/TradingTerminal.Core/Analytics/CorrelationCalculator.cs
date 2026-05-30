using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Analytics;

/// <summary>
/// Pure correlation math for the Correlation Matrix tool. No UI or broker dependencies — keyed off
/// plain <see cref="Bar"/> series so it stays fully unit-testable.
///
/// Pipeline: <see cref="AlignByTimestamp"/> (intersect timestamps so every instrument shares the
/// same rows) → <see cref="LogReturns"/> (bar-to-bar log returns, the stationary basis) →
/// <see cref="PearsonMatrix"/>. Zero-variance series correlate to 0 rather than NaN so a flat
/// instrument never poisons the whole grid.
/// </summary>
public static class CorrelationCalculator
{
    /// <summary>Bar-to-bar log returns <c>ln(c_t / c_{t-1})</c>. Returns an array of length
    /// <c>closes.Count - 1</c> (empty if fewer than two prices). Non-positive prices yield a 0
    /// return for that step rather than NaN/-inf.</summary>
    public static IReadOnlyList<double> LogReturns(IReadOnlyList<double> closes)
    {
        if (closes is null || closes.Count < 2)
            return Array.Empty<double>();

        var returns = new double[closes.Count - 1];
        for (int i = 1; i < closes.Count; i++)
        {
            double prev = closes[i - 1];
            double cur = closes[i];
            returns[i - 1] = (prev > 0.0 && cur > 0.0) ? Math.Log(cur / prev) : 0.0;
        }
        return returns;
    }

    /// <summary>
    /// Aligns several bar series onto their common set of timestamps. Different instruments rarely
    /// share identical bars (holidays, half-days, feed gaps), so without this the close arrays would
    /// be misaligned and the correlations meaningless. Returns the shared timestamps (ascending) and,
    /// for each input series, the closes at exactly those timestamps. Duplicate timestamps within a
    /// series keep the last close.
    /// </summary>
    public static (IReadOnlyList<DateTime> Timestamps, double[][] AlignedCloses) AlignByTimestamp(
        IReadOnlyList<IReadOnlyList<Bar>> series)
    {
        if (series is null || series.Count == 0)
            return (Array.Empty<DateTime>(), Array.Empty<double[]>());

        // Per-series timestamp → close lookup (last write wins on duplicate timestamps).
        var maps = new Dictionary<DateTime, double>[series.Count];
        for (int s = 0; s < series.Count; s++)
        {
            var map = new Dictionary<DateTime, double>();
            foreach (var bar in series[s])
                map[bar.TimestampUtc] = bar.Close;
            maps[s] = map;
        }

        // Common timestamps = intersection across every series.
        IEnumerable<DateTime> common = maps[0].Keys;
        for (int s = 1; s < maps.Length; s++)
            common = common.Intersect(maps[s].Keys);

        var timestamps = common.OrderBy(t => t).ToArray();

        var aligned = new double[series.Count][];
        for (int s = 0; s < series.Count; s++)
        {
            var closes = new double[timestamps.Length];
            for (int i = 0; i < timestamps.Length; i++)
                closes[i] = maps[s][timestamps[i]];
            aligned[s] = closes;
        }

        return (timestamps, aligned);
    }

    /// <summary>Pearson correlation of two equal-length series. Returns 0 when either series has
    /// zero variance (or they differ in length / are too short) so callers never propagate NaN.</summary>
    public static double Pearson(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a is null || b is null || a.Count != b.Count || a.Count < 2)
            return 0.0;

        int n = a.Count;
        double meanA = 0.0, meanB = 0.0;
        for (int i = 0; i < n; i++) { meanA += a[i]; meanB += b[i]; }
        meanA /= n; meanB /= n;

        double cov = 0.0, varA = 0.0, varB = 0.0;
        for (int i = 0; i < n; i++)
        {
            double da = a[i] - meanA;
            double db = b[i] - meanB;
            cov += da * db;
            varA += da * da;
            varB += db * db;
        }

        double denom = Math.Sqrt(varA * varB);
        if (denom <= double.Epsilon)
            return 0.0;

        // Clamp to guard against tiny floating-point overshoot beyond ±1.
        return Math.Clamp(cov / denom, -1.0, 1.0);
    }

    /// <summary>Builds the symmetric NxN Pearson correlation matrix for the supplied return series.
    /// All series must already be aligned/equal length. The diagonal is exactly 1.0.</summary>
    public static double[,] PearsonMatrix(IReadOnlyList<IReadOnlyList<double>> returnSeries)
    {
        int n = returnSeries?.Count ?? 0;
        var matrix = new double[n, n];

        for (int i = 0; i < n; i++)
        {
            matrix[i, i] = 1.0;
            for (int j = i + 1; j < n; j++)
            {
                double r = Pearson(returnSeries![i], returnSeries[j]);
                matrix[i, j] = r;
                matrix[j, i] = r;
            }
        }

        return matrix;
    }
}
