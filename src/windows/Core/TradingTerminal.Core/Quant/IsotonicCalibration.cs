namespace TradingTerminal.Core.Quant;

/// <summary>
/// A monotone calibration map C → E[forward return], represented as breakpoints (sorted x with
/// fitted y) plus the per-region sample counts. Evaluate via <see cref="Evaluate"/> (linear
/// interpolation between breakpoints; flat extrapolation beyond the ends).
/// </summary>
/// <param name="X">Sorted composite values at the fitted knots.</param>
/// <param name="Y">Fitted (monotone non-decreasing) expected returns at each knot.</param>
/// <param name="Counts">Number of raw samples backing each knot/block.</param>
/// <param name="TotalSamples">Total samples used to fit.</param>
public sealed record IsotonicMap(double[] X, double[] Y, int[] Counts, int TotalSamples)
{
    /// <summary>g(C): the calibrated expected forward return at composite C (interpolated).</summary>
    public double Evaluate(double c)
    {
        if (X.Length == 0) return 0;
        if (X.Length == 1 || c <= X[0]) return Y[0];
        if (c >= X[^1]) return Y[^1];
        // Binary search for the bracketing interval.
        int lo = 0, hi = X.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (X[mid] <= c) lo = mid; else hi = mid;
        }
        var x0 = X[lo]; var x1 = X[hi];
        var t = x1 - x0 > 1e-300 ? (c - x0) / (x1 - x0) : 0.0;
        return Y[lo] + t * (Y[hi] - Y[lo]);
    }
}

/// <summary>
/// Isotonic (monotone) calibration of a composite score C onto the expected forward return,
/// g(C) = E[r | C], via the pool-adjacent-violators algorithm (PAVA). PAVA finds the
/// least-squares non-decreasing fit by repeatedly averaging adjacent blocks that violate
/// monotonicity — the standard tool for turning a raw, noisy score→outcome scatter into a
/// well-behaved monotone calibration curve. A binned-mean fallback is provided for small samples,
/// and per-region counts let callers detect "bootstrap mode" (too little history to trust the
/// calibration).
/// </summary>
public static class IsotonicCalibration
{
    /// <summary>
    /// Fits an isotonic map from paired (composite, forwardReturn) samples. Points are sorted by
    /// composite, then PAVA enforces a non-decreasing fit. <paramref name="minSamples"/> is carried
    /// through for callers to interpret bootstrap mode; the fit still runs on whatever is given.
    /// </summary>
    public static IsotonicMap Fit(IReadOnlyList<double> composites, IReadOnlyList<double> forwardReturns, int minSamples = 100)
    {
        ArgumentNullException.ThrowIfNull(composites);
        ArgumentNullException.ThrowIfNull(forwardReturns);
        if (composites.Count != forwardReturns.Count) throw new ArgumentException("composites and forwardReturns must have equal length.");
        var n = composites.Count;
        if (n == 0) return new IsotonicMap(Array.Empty<double>(), Array.Empty<double>(), Array.Empty<int>(), 0);

        var order = new int[n];
        for (var i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => composites[a].CompareTo(composites[b]));

        // PAVA on the sorted y, blocks carry (weightedSumY, weight, anchorX).
        var blkY = new double[n];      // block mean
        var blkW = new double[n];      // block weight (count)
        var blkX = new double[n];      // representative x (last/anchor x of block)
        var top = 0;
        for (var k = 0; k < n; k++)
        {
            var y = forwardReturns[order[k]];
            var x = composites[order[k]];
            blkY[top] = y; blkW[top] = 1; blkX[top] = x; top++;
            // Pool while the previous block exceeds the current (violation).
            while (top > 1 && blkY[top - 2] > blkY[top - 1])
            {
                var w = blkW[top - 2] + blkW[top - 1];
                var ym = (blkY[top - 2] * blkW[top - 2] + blkY[top - 1] * blkW[top - 1]) / w;
                blkY[top - 2] = ym;
                blkW[top - 2] = w;
                blkX[top - 2] = blkX[top - 1]; // anchor at the block's upper edge
                top--;
            }
        }

        var xs = new double[top];
        var ys = new double[top];
        var counts = new int[top];
        for (var b = 0; b < top; b++) { xs[b] = blkX[b]; ys[b] = blkY[b]; counts[b] = (int)blkW[b]; }
        return new IsotonicMap(xs, ys, counts, n);
    }

    /// <summary>
    /// Binned-mean fallback calibration: splits the composite range into <paramref name="bins"/>
    /// equal-width bins and takes the mean forward return per non-empty bin. Not guaranteed
    /// monotone — a simpler, lower-variance map for when sample counts are too low for PAVA to be
    /// meaningful. Empty bins are dropped.
    /// </summary>
    public static IsotonicMap BinnedMean(IReadOnlyList<double> composites, IReadOnlyList<double> forwardReturns, int bins = 10)
    {
        ArgumentNullException.ThrowIfNull(composites);
        ArgumentNullException.ThrowIfNull(forwardReturns);
        if (composites.Count != forwardReturns.Count) throw new ArgumentException("composites and forwardReturns must have equal length.");
        if (bins < 1) throw new ArgumentOutOfRangeException(nameof(bins));
        var n = composites.Count;
        if (n == 0) return new IsotonicMap(Array.Empty<double>(), Array.Empty<double>(), Array.Empty<int>(), 0);

        double min = double.MaxValue, max = double.MinValue;
        for (var i = 0; i < n; i++) { min = Math.Min(min, composites[i]); max = Math.Max(max, composites[i]); }
        var width = (max - min) / bins;
        if (width <= 1e-300) // all equal — single point
            return new IsotonicMap(new[] { min }, new[] { Mean(forwardReturns) }, new[] { n }, n);

        var sum = new double[bins];
        var cnt = new int[bins];
        for (var i = 0; i < n; i++)
        {
            var bin = Math.Clamp((int)((composites[i] - min) / width), 0, bins - 1);
            sum[bin] += forwardReturns[i];
            cnt[bin]++;
        }

        var xs = new List<double>(bins);
        var ys = new List<double>(bins);
        var counts = new List<int>(bins);
        for (var b = 0; b < bins; b++)
        {
            if (cnt[b] == 0) continue;
            xs.Add(min + (b + 0.5) * width);
            ys.Add(sum[b] / cnt[b]);
            counts.Add(cnt[b]);
        }
        return new IsotonicMap(xs.ToArray(), ys.ToArray(), counts.ToArray(), n);
    }

    private static double Mean(IReadOnlyList<double> v)
    {
        if (v.Count == 0) return 0;
        double s = 0; for (var i = 0; i < v.Count; i++) s += v[i];
        return s / v.Count;
    }
}
