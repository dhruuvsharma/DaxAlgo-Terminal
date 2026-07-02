namespace TradingTerminal.Core.Quant.Surfaces;

/// <summary>How a surface axis value should be rendered (tick labels, tooltips, stats).</summary>
public enum SurfaceAxisFormat
{
    /// <summary>Fractional value shown as a percentage (0.0123 → "1.23%").</summary>
    Percent,
    /// <summary>Dimensionless ratio (z-score, autocorrelation) shown as "0.00".</summary>
    Ratio,
    /// <summary>Whole number (periods, counts) shown as "0".</summary>
    Integer,
    /// <summary>Plain number with adaptive precision.</summary>
    Number,
}

/// <summary>Formatting helper shared by the 3D axis labels, slice charts, and stats panels.</summary>
public static class SurfaceAxisFormats
{
    public static string Format(double value, SurfaceAxisFormat format)
    {
        if (double.IsNaN(value)) return "—";
        return format switch
        {
            SurfaceAxisFormat.Percent => value.ToString("+0.00%;-0.00%;0.00%"),
            SurfaceAxisFormat.Ratio   => value.ToString("0.00"),
            SurfaceAxisFormat.Integer => Math.Round(value).ToString("0"),
            _ => Math.Abs(value) >= 1000 ? value.ToString("N0")
               : Math.Abs(value) >= 1    ? value.ToString("0.00")
               : value.ToString("0.####"),
        };
    }
}

/// <summary>
/// Everything a per-cell statistic is computed from: the bucket of realized (next-period) returns
/// that fell into the cell, the aligned dollar volumes (for Amihud), and the annualization factor.
/// </summary>
public sealed record SurfaceCellSample(
    double[] Returns,
    double[]? DollarVolumes,
    double PeriodsPerYear)
{
    public static readonly SurfaceCellSample Empty = new(Array.Empty<double>(), null, 252);
}

/// <summary>Registry category — drives the grouped dropdowns in the Surface Lab UI.</summary>
public enum SurfaceMetricCategory
{
    /// <summary>First moments / counts of the bucket (natural Z-axis picks).</summary>
    Aggregate,
    /// <summary>Distribution shape, tail risk, dependence, and liquidity statistics (Z or W axis).</summary>
    Statistical,
}

/// <summary>One selectable statistic: id (also the formula-bar variable name), display name,
/// category, display format, and the compute function over a cell sample.</summary>
public sealed record SurfaceMetricDefinition(
    string Id,
    string Name,
    SurfaceMetricCategory Category,
    SurfaceAxisFormat Format,
    Func<SurfaceCellSample, double> Compute);

/// <summary>
/// The statistics registry behind the Z / W axis dropdowns and the formula-bar variables.
/// Pure math over a bucket of returns — no strategy/portfolio metrics live here. All functions
/// are NaN-safe: degenerate samples (empty, zero variance) yield NaN, and the renderer paints
/// NaN cells as gaps rather than lying with a zero.
/// </summary>
public static class SurfaceMetricRegistry
{
    public static IReadOnlyList<SurfaceMetricDefinition> All { get; } = Build();

    public static SurfaceMetricDefinition? Resolve(string id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    private static List<SurfaceMetricDefinition> Build() => new()
    {
        // ── Bucket aggregates ───────────────────────────────────────────────────────────────
        new("avgret",    "Average Return",       SurfaceMetricCategory.Aggregate,   SurfaceAxisFormat.Percent, Mean),
        new("medret",    "Median Return",        SurfaceMetricCategory.Aggregate,   SurfaceAxisFormat.Percent, Median),
        new("stdret",    "Std Dev of Returns",   SurfaceMetricCategory.Aggregate,   SurfaceAxisFormat.Percent, StdDev),
        new("count",     "Frequency (count)",    SurfaceMetricCategory.Aggregate,   SurfaceAxisFormat.Integer, s => s.Returns.Length),
        new("probup",    "P(return > 0)",        SurfaceMetricCategory.Aggregate,   SurfaceAxisFormat.Percent, ProbUp),

        // ── Statistical / risk ──────────────────────────────────────────────────────────────
        new("vol",       "Realized Vol (ann.)",  SurfaceMetricCategory.Statistical, SurfaceAxisFormat.Percent, RealizedVol),
        new("var95",     "VaR 95%",              SurfaceMetricCategory.Statistical, SurfaceAxisFormat.Percent, s => ValueAtRisk(s, 0.95)),
        new("var99",     "VaR 99%",              SurfaceMetricCategory.Statistical, SurfaceAxisFormat.Percent, s => ValueAtRisk(s, 0.99)),
        new("cvar95",    "CVaR 95% (ES)",        SurfaceMetricCategory.Statistical, SurfaceAxisFormat.Percent, s => ConditionalVaR(s, 0.95)),
        new("skew",      "Skewness",             SurfaceMetricCategory.Statistical, SurfaceAxisFormat.Ratio,   Skewness),
        new("kurtosis",  "Kurtosis (excess)",    SurfaceMetricCategory.Statistical, SurfaceAxisFormat.Ratio,   Kurtosis),
        new("zscore",    "Z-Score of mean",      SurfaceMetricCategory.Statistical, SurfaceAxisFormat.Ratio,   ZScore),
        new("npdf",      "Normal PDF (z)",       SurfaceMetricCategory.Statistical, SurfaceAxisFormat.Number,  s => NormalPdf(ZScore(s))),
        new("ncdf",      "Normal CDF (z)",       SurfaceMetricCategory.Statistical, SurfaceAxisFormat.Percent, s => NormalCdf(ZScore(s))),
        new("autocorr1", "Autocorrelation (lag 1)", SurfaceMetricCategory.Statistical, SurfaceAxisFormat.Ratio, Autocorr1),
        new("amihud",    "Amihud Illiquidity",   SurfaceMetricCategory.Statistical, SurfaceAxisFormat.Number,  Amihud),
    };

    // ── Core math (all static, allocation-light, NaN on degenerate input) ────────────────────

    public static double Mean(SurfaceCellSample s) =>
        s.Returns.Length == 0 ? double.NaN : s.Returns.Average();

    public static double Median(SurfaceCellSample s)
    {
        if (s.Returns.Length == 0) return double.NaN;
        var sorted = (double[])s.Returns.Clone();
        Array.Sort(sorted);
        var n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : 0.5 * (sorted[n / 2 - 1] + sorted[n / 2]);
    }

    public static double StdDev(SurfaceCellSample s) => StdDev(s.Returns);

    private static double StdDev(double[] r)
    {
        if (r.Length < 2) return double.NaN;
        var mean = r.Average();
        var ss = 0.0;
        foreach (var v in r) ss += (v - mean) * (v - mean);
        return Math.Sqrt(ss / (r.Length - 1));
    }

    public static double ProbUp(SurfaceCellSample s) =>
        s.Returns.Length == 0 ? double.NaN : s.Returns.Count(v => v > 0) / (double)s.Returns.Length;

    public static double RealizedVol(SurfaceCellSample s)
    {
        var sd = StdDev(s.Returns);
        return double.IsNaN(sd) ? double.NaN : sd * Math.Sqrt(s.PeriodsPerYear);
    }

    /// <summary>Historical VaR at the given confidence, as a positive loss fraction.</summary>
    public static double ValueAtRisk(SurfaceCellSample s, double confidence)
    {
        if (s.Returns.Length < 5) return double.NaN;
        var sorted = (double[])s.Returns.Clone();
        Array.Sort(sorted);
        var idx = (int)Math.Floor((1 - confidence) * sorted.Length);
        idx = Math.Clamp(idx, 0, sorted.Length - 1);
        return -sorted[idx];
    }

    /// <summary>Expected shortfall: mean loss beyond the VaR quantile, positive fraction.</summary>
    public static double ConditionalVaR(SurfaceCellSample s, double confidence)
    {
        if (s.Returns.Length < 5) return double.NaN;
        var sorted = (double[])s.Returns.Clone();
        Array.Sort(sorted);
        var cut = Math.Max(1, (int)Math.Floor((1 - confidence) * sorted.Length));
        var sum = 0.0;
        for (var i = 0; i < cut; i++) sum += sorted[i];
        return -(sum / cut);
    }

    public static double Skewness(SurfaceCellSample s)
    {
        var r = s.Returns;
        if (r.Length < 3) return double.NaN;
        var mean = r.Average();
        double m2 = 0, m3 = 0;
        foreach (var v in r)
        {
            var d = v - mean;
            m2 += d * d;
            m3 += d * d * d;
        }
        m2 /= r.Length;
        m3 /= r.Length;
        return m2 <= 0 ? double.NaN : m3 / Math.Pow(m2, 1.5);
    }

    public static double Kurtosis(SurfaceCellSample s)
    {
        var r = s.Returns;
        if (r.Length < 4) return double.NaN;
        var mean = r.Average();
        double m2 = 0, m4 = 0;
        foreach (var v in r)
        {
            var d = v - mean;
            m2 += d * d;
            m4 += d * d * d * d;
        }
        m2 /= r.Length;
        m4 /= r.Length;
        return m2 <= 0 ? double.NaN : m4 / (m2 * m2) - 3;
    }

    /// <summary>t-like statistic of the mean: mean / (std / √n).</summary>
    public static double ZScore(SurfaceCellSample s)
    {
        var sd = StdDev(s.Returns);
        if (double.IsNaN(sd) || sd <= 0) return double.NaN;
        return Mean(s) / (sd / Math.Sqrt(s.Returns.Length));
    }

    public static double NormalPdf(double z) =>
        double.IsNaN(z) ? double.NaN : Math.Exp(-0.5 * z * z) / Math.Sqrt(2 * Math.PI);

    /// <summary>Abramowitz–Stegun 7.1.26 erf approximation — |error| &lt; 1.5e-7.</summary>
    public static double NormalCdf(double z)
    {
        if (double.IsNaN(z)) return double.NaN;
        var x = z / Math.Sqrt(2);
        var t = 1 / (1 + 0.3275911 * Math.Abs(x));
        var erf = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        if (x < 0) erf = -erf;
        return 0.5 * (1 + erf);
    }

    /// <summary>Lag-1 sample autocorrelation of the cell's returns (arrival order).</summary>
    public static double Autocorr1(SurfaceCellSample s)
    {
        var r = s.Returns;
        if (r.Length < 3) return double.NaN;
        var mean = r.Average();
        double num = 0, den = 0;
        for (var i = 0; i < r.Length; i++)
        {
            var d = r[i] - mean;
            den += d * d;
            if (i > 0) num += d * (r[i - 1] - mean);
        }
        return den <= 0 ? double.NaN : num / den;
    }

    /// <summary>Amihud (2002) illiquidity: mean(|r| / dollar volume) × 10⁶.</summary>
    public static double Amihud(SurfaceCellSample s)
    {
        var dv = s.DollarVolumes;
        var r = s.Returns;
        if (dv is null || dv.Length != r.Length || r.Length == 0) return double.NaN;
        var sum = 0.0;
        var n = 0;
        for (var i = 0; i < r.Length; i++)
        {
            if (dv[i] <= 0) continue;
            sum += Math.Abs(r[i]) / dv[i];
            n++;
        }
        return n == 0 ? double.NaN : sum / n * 1e6;
    }
}
