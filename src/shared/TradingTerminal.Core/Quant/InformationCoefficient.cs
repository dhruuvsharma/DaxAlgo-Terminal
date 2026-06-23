namespace TradingTerminal.Core.Quant;

/// <summary>
/// Information-coefficient result for one signal.
/// </summary>
/// <param name="Pearson">Pearson IC: linear correlation of signal score with forward return.</param>
/// <param name="Spearman">Spearman (rank) IC; robust to outliers/non-linearity.</param>
/// <param name="SampleSize">Number of valid (non-stale) paired observations used.</param>
public sealed record IcResult(double Pearson, double Spearman, int SampleSize);

/// <summary>
/// Information coefficient: the rolling correlation of a signal's score with the forward return at
/// horizon h. IC is the workhorse skill metric in quant signal research (Grinold-Kahn): a positive
/// IC means the signal tends to be right about direction/magnitude. This computes both the Pearson
/// (linear) and Spearman (rank) flavours, in scalar and vector (k-signal) forms, and skips
/// missing/stale entries (encoded as <see cref="double.NaN"/>).
/// </summary>
public static class InformationCoefficient
{
    /// <summary>
    /// IC of one signal vs forward return. Pairs are skipped where either value is NaN. Returns a
    /// zero result when fewer than 2 valid pairs remain.
    /// </summary>
    public static IcResult Compute(IReadOnlyList<double> scores, IReadOnlyList<double> forwardReturns)
    {
        ArgumentNullException.ThrowIfNull(scores);
        ArgumentNullException.ThrowIfNull(forwardReturns);
        if (scores.Count != forwardReturns.Count) throw new ArgumentException("scores and forwardReturns must have equal length.");

        var xs = new List<double>(scores.Count);
        var ys = new List<double>(scores.Count);
        for (var i = 0; i < scores.Count; i++)
        {
            var a = scores[i]; var b = forwardReturns[i];
            if (double.IsNaN(a) || double.IsNaN(b)) continue;
            xs.Add(a); ys.Add(b);
        }
        if (xs.Count < 2) return new IcResult(0, 0, xs.Count);

        var pearson = Pearson(xs, ys);
        var spearman = Pearson(Rank(xs), Rank(ys));
        return new IcResult(pearson, spearman, xs.Count);
    }

    /// <summary>
    /// Vector IC for k signals against a shared forward-return series. <paramref name="scoreColumns"/>
    /// is row-major (rows = observations, columns = signals). Each column is paired independently
    /// against <paramref name="forwardReturns"/>, skipping NaNs per column.
    /// </summary>
    public static IcResult[] Compute(double[,] scoreColumns, IReadOnlyList<double> forwardReturns)
    {
        ArgumentNullException.ThrowIfNull(scoreColumns);
        ArgumentNullException.ThrowIfNull(forwardReturns);
        var n = scoreColumns.GetLength(0);
        var k = scoreColumns.GetLength(1);
        if (n != forwardReturns.Count) throw new ArgumentException("scoreColumns rows must match forwardReturns length.");

        var results = new IcResult[k];
        var col = new double[n];
        for (var j = 0; j < k; j++)
        {
            for (var i = 0; i < n; i++) col[i] = scoreColumns[i, j];
            results[j] = Compute(col, forwardReturns);
        }
        return results;
    }

    private static double Pearson(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = x.Count;
        if (n < 2) return 0;
        double mx = 0, my = 0;
        for (var i = 0; i < n; i++) { mx += x[i]; my += y[i]; }
        mx /= n; my /= n;
        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            var dx = x[i] - mx; var dy = y[i] - my;
            sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
        }
        var denom = Math.Sqrt(sxx * syy);
        return denom > 1e-300 ? Math.Clamp(sxy / denom, -1.0, 1.0) : 0.0;
    }

    /// <summary>Fractional ranks with tie-averaging.</summary>
    private static double[] Rank(IReadOnlyList<double> values)
    {
        var n = values.Count;
        var idx = new int[n];
        for (var i = 0; i < n; i++) idx[i] = i;
        Array.Sort(idx, (a, b) => values[a].CompareTo(values[b]));
        var ranks = new double[n];
        var i2 = 0;
        while (i2 < n)
        {
            var j = i2;
            while (j + 1 < n && values[idx[j + 1]] == values[idx[i2]]) j++;
            var avg = (i2 + j) / 2.0 + 1.0; // 1-based average rank over the tie block
            for (var t = i2; t <= j; t++) ranks[idx[t]] = avg;
            i2 = j + 1;
        }
        return ranks;
    }
}
