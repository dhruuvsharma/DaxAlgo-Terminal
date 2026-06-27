namespace TradingTerminal.Core.Quant.TimeSeries;

/// <summary>Stationarity-inducing transform applied to a price series before testing/modelling.</summary>
public enum SeriesTransform
{
    /// <summary>Raw price level (usually non-stationary — the baseline to compare against).</summary>
    Level,

    /// <summary>ln(price) — stabilizes multiplicative variance, still trending.</summary>
    Log,

    /// <summary>pₜ − pₜ₋₁ — classic d=1 differencing.</summary>
    FirstDifference,

    /// <summary>ln(pₜ/pₜ₋₁) — the house-rule statistical series.</summary>
    LogReturns,

    /// <summary>
    /// Fractionally differenced log price (fixed-width window, López de Prado ch.5) — removes
    /// just enough memory to pass ADF while keeping most of the level information that full
    /// d=1 differencing throws away.
    /// </summary>
    FractionalDifference,
}

/// <summary>
/// Price-series transforms for the stationarity tooling, plus the rolling moments used to
/// visualize drift in mean/variance. All pure; outputs are shorter than inputs where a transform
/// consumes leading observations (the caller aligns timestamps by skipping the same count).
/// </summary>
public static class SeriesTransforms
{
    /// <summary>
    /// Applies <paramref name="transform"/>. Returns the transformed series and how many leading
    /// input observations were consumed (so timestamps can be re-aligned).
    /// <paramref name="fracD"/> only applies to <see cref="SeriesTransform.FractionalDifference"/>.
    /// </summary>
    public static (double[] Series, int Consumed) Apply(
        IReadOnlyList<double> prices, SeriesTransform transform, double fracD = 0.4)
    {
        var n = prices.Count;
        switch (transform)
        {
            case SeriesTransform.Level:
            {
                var outp = new double[n];
                for (var i = 0; i < n; i++) outp[i] = prices[i];
                return (outp, 0);
            }
            case SeriesTransform.Log:
            {
                var outp = new double[n];
                for (var i = 0; i < n; i++) outp[i] = SafeLog(prices[i]);
                return (outp, 0);
            }
            case SeriesTransform.FirstDifference:
            {
                if (n < 2) return (Array.Empty<double>(), n);
                var outp = new double[n - 1];
                for (var i = 1; i < n; i++) outp[i - 1] = prices[i] - prices[i - 1];
                return (outp, 1);
            }
            case SeriesTransform.LogReturns:
            {
                if (n < 2) return (Array.Empty<double>(), n);
                var outp = new double[n - 1];
                for (var i = 1; i < n; i++) outp[i - 1] = SafeLog(prices[i]) - SafeLog(prices[i - 1]);
                return (outp, 1);
            }
            case SeriesTransform.FractionalDifference:
            {
                var logp = new double[n];
                for (var i = 0; i < n; i++) logp[i] = SafeLog(prices[i]);
                return FracDiff(logp, fracD);
            }
            default:
                return (Array.Empty<double>(), n);
        }
    }

    /// <summary>
    /// Fixed-width-window fractional differencing: weights w₀=1, w_k = −w_{k−1}·(d−k+1)/k,
    /// truncated once |w_k| &lt; <paramref name="weightThreshold"/>. The first (window−1) outputs
    /// are dropped (consumed) so every emitted value uses the full weight window.
    /// </summary>
    public static (double[] Series, int Consumed) FracDiff(
        IReadOnlyList<double> series, double d, double weightThreshold = 1e-4)
    {
        var n = series.Count;
        if (n < 2) return (Array.Empty<double>(), n);

        var weights = new List<double> { 1.0 };
        for (var k = 1; k < n; k++)
        {
            var w = -weights[^1] * (d - k + 1) / k;
            if (Math.Abs(w) < weightThreshold) break;
            weights.Add(w);
        }

        var window = weights.Count;
        if (n <= window) return (Array.Empty<double>(), n);

        var outp = new double[n - window + 1];
        for (var t = window - 1; t < n; t++)
        {
            double s = 0;
            for (var k = 0; k < window; k++) s += weights[k] * series[t - k];
            outp[t - (window - 1)] = s;
        }
        return (outp, window - 1);
    }

    /// <summary>
    /// Rolling mean and (population) standard deviation over a trailing <paramref name="window"/>,
    /// aligned to the input (the first window−1 slots are NaN). O(n) via running sums with a
    /// guarded variance (negative round-off clamps to 0).
    /// </summary>
    public static (double[] Mean, double[] Std) RollingMeanStd(IReadOnlyList<double> series, int window)
    {
        var n = series.Count;
        var mean = new double[n];
        var std = new double[n];
        Array.Fill(mean, double.NaN);
        Array.Fill(std, double.NaN);
        if (window < 2 || n < window) return (mean, std);

        double sum = 0, sumSq = 0;
        for (var i = 0; i < n; i++)
        {
            sum += series[i];
            sumSq += series[i] * series[i];
            if (i >= window)
            {
                sum -= series[i - window];
                sumSq -= series[i - window] * series[i - window];
            }
            if (i >= window - 1)
            {
                var m = sum / window;
                var v = sumSq / window - m * m;
                mean[i] = m;
                std[i] = v > 0 ? Math.Sqrt(v) : 0.0;
            }
        }
        return (mean, std);
    }

    private static double SafeLog(double p) => Math.Log(Math.Max(p, 1e-12));
}
