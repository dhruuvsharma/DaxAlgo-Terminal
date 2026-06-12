namespace TradingTerminal.Core.Quant.TimeSeries;

/// <summary>
/// Outcome of a unit-root / stationarity test. <see cref="Statistic"/> is compared against the
/// asymptotic <see cref="CriticalValues"/> (keyed by significance level, e.g. 0.05). For ADF the
/// null is "unit root" (reject ⇒ stationary, statistic more negative than the CV); for KPSS the
/// null is "stationary" (reject ⇒ non-stationary, statistic above the CV) — <see cref="IsStationary"/>
/// already encodes the right direction at 5%.
/// </summary>
public sealed record StationarityTestResult(
    string Test,
    double Statistic,
    IReadOnlyDictionary<double, double> CriticalValues,
    bool IsStationary,
    int LagsUsed,
    int N);

/// <summary>
/// Augmented Dickey-Fuller and KPSS stationarity tests plus the sample autocorrelation function —
/// the standard "is this series modellable?" toolkit. Pure, deterministic, no allocation beyond
/// the result. Numerics follow the quant-math house rules (single-pass moments, guarded divisors).
/// </summary>
public static class StationarityTests
{
    /// <summary>
    /// ADF asymptotic critical values, constant-only regression (MacKinnon 2010, T→∞).
    /// The test statistic must be MORE NEGATIVE than the CV to reject the unit root.
    /// </summary>
    private static readonly IReadOnlyDictionary<double, double> AdfCriticalValues =
        new Dictionary<double, double> { [0.01] = -3.430, [0.05] = -2.862, [0.10] = -2.567 };

    /// <summary>KPSS level-stationarity critical values (Kwiatkowski et al. 1992, Table 1, η_μ).</summary>
    private static readonly IReadOnlyDictionary<double, double> KpssCriticalValues =
        new Dictionary<double, double> { [0.01] = 0.739, [0.05] = 0.463, [0.10] = 0.347 };

    /// <summary>
    /// Augmented Dickey-Fuller test with a constant:
    /// <c>Δyₜ = α + γ·yₜ₋₁ + Σᵢ φᵢ·Δyₜ₋ᵢ + εₜ</c>; the statistic is the t-ratio of γ.
    /// <paramref name="lags"/> &lt; 0 selects the augmentation order by AIC over 0..Schwert
    /// max-lag <c>⌊12·(n/100)^¼⌋</c>. Needs ~25+ observations; returns null below that or on a
    /// degenerate (constant) series.
    /// </summary>
    public static StationarityTestResult? Adf(IReadOnlyList<double> series, int lags = -1)
    {
        var n = series.Count;
        if (n < 25) return null;

        var maxLag = lags >= 0
            ? lags
            : Math.Min((int)Math.Floor(12.0 * Math.Pow(n / 100.0, 0.25)), (n - 10) / 2);
        if (maxLag < 0) maxLag = 0;

        if (lags >= 0)
        {
            var fixedFit = AdfRegression(series, lags);
            return fixedFit is null
                ? null
                : Build("ADF", fixedFit.Value.TStat, AdfCriticalValues, lags, n);
        }

        // AIC selection over augmentation order, all fitted on the common sample (maxLag offset)
        // so the criteria are comparable.
        (double TStat, double Aic)? best = null;
        var bestLag = 0;
        for (var l = 0; l <= maxLag; l++)
        {
            var fit = AdfRegression(series, l, sampleOffset: maxLag);
            if (fit is null) continue;
            if (best is null || fit.Value.Aic < best.Value.Aic)
            {
                best = fit;
                bestLag = l;
            }
        }
        if (best is null) return null;

        // Re-fit at the chosen order on the full usable sample for the final statistic.
        var final = AdfRegression(series, bestLag);
        if (final is null) return null;
        return Build("ADF", final.Value.TStat, AdfCriticalValues, bestLag, n);
    }

    /// <summary>One ADF regression at augmentation order <paramref name="lags"/> → (t-stat of γ, AIC).</summary>
    private static (double TStat, double Aic)? AdfRegression(
        IReadOnlyList<double> series, int lags, int sampleOffset = -1)
    {
        var n = series.Count;
        var start = 1 + Math.Max(lags, sampleOffset < 0 ? lags : sampleOffset);
        var rows = n - start;
        var k = 2 + lags; // constant, y_{t-1}, lagged differences
        if (rows <= k + 2) return null;

        var x = new double[rows][];
        var y = new double[rows];
        for (var i = 0; i < rows; i++)
        {
            var t = start + i;
            y[i] = series[t] - series[t - 1];
            var row = new double[k];
            row[0] = 1.0;
            row[1] = series[t - 1];
            for (var j = 1; j <= lags; j++)
                row[1 + j] = series[t - j] - series[t - j - 1];
            x[i] = row;
        }

        var fit = Ols.Fit(x, y);
        if (fit is null || fit.StandardErrors[1] <= 1e-300) return null;
        var aic = rows * Math.Log(Math.Max(fit.Rss / rows, 1e-300)) + 2.0 * k;
        return (fit.TStat(1), aic);
    }

    /// <summary>
    /// KPSS level-stationarity test: <c>η = Σₜ Sₜ² / (n²·σ̂²_lr)</c> where Sₜ are partial sums of
    /// the demeaned series and σ̂²_lr is the Bartlett-kernel long-run variance with bandwidth
    /// <c>⌊4·(n/100)^¼⌋</c> (pass <paramref name="lags"/> ≥ 0 to pin it). Null = stationary, so
    /// the verdict rejects when η exceeds the 5% critical value. Needs ~25+ observations.
    /// </summary>
    public static StationarityTestResult? Kpss(IReadOnlyList<double> series, int lags = -1)
    {
        var n = series.Count;
        if (n < 25) return null;

        double mean = 0;
        for (var i = 0; i < n; i++) mean += series[i];
        mean /= n;

        var e = new double[n];
        for (var i = 0; i < n; i++) e[i] = series[i] - mean;

        // Partial-sum numerator.
        double s = 0, sumS2 = 0;
        for (var i = 0; i < n; i++)
        {
            s += e[i];
            sumS2 += s * s;
        }

        // Bartlett long-run variance of the residuals.
        var l = lags >= 0 ? lags : (int)Math.Floor(4.0 * Math.Pow(n / 100.0, 0.25));
        if (l >= n) l = n - 1;
        double lrVar = 0;
        for (var i = 0; i < n; i++) lrVar += e[i] * e[i];
        for (var lag = 1; lag <= l; lag++)
        {
            double gamma = 0;
            for (var i = lag; i < n; i++) gamma += e[i] * e[i - lag];
            lrVar += 2.0 * (1.0 - (double)lag / (l + 1)) * gamma;
        }
        lrVar /= n;
        if (lrVar <= 1e-300) return null;

        var eta = sumS2 / (n * (double)n * lrVar);
        return Build("KPSS", eta, KpssCriticalValues, l, n);
    }

    /// <summary>
    /// Sample autocorrelation function ρ₁..ρ_K (lag 0 omitted — it's 1 by construction). The
    /// approximate white-noise 95% band is ±1.96/√n (<see cref="AcfConfidenceBand"/>).
    /// </summary>
    public static double[] Acf(IReadOnlyList<double> series, int maxLag)
    {
        var n = series.Count;
        maxLag = Math.Min(maxLag, n - 2);
        if (n < 3 || maxLag < 1) return Array.Empty<double>();

        double mean = 0;
        for (var i = 0; i < n; i++) mean += series[i];
        mean /= n;

        double gamma0 = 0;
        for (var i = 0; i < n; i++) gamma0 += (series[i] - mean) * (series[i] - mean);
        if (gamma0 <= 1e-300) return new double[maxLag];

        var acf = new double[maxLag];
        for (var lag = 1; lag <= maxLag; lag++)
        {
            double g = 0;
            for (var i = lag; i < n; i++) g += (series[i] - mean) * (series[i - lag] - mean);
            acf[lag - 1] = g / gamma0;
        }
        return acf;
    }

    public static double AcfConfidenceBand(int n) => n > 0 ? 1.96 / Math.Sqrt(n) : 0.0;

    private static StationarityTestResult Build(
        string test, double stat, IReadOnlyDictionary<double, double> cv, int lags, int n)
    {
        // Both tests verdict "stationary" below the 5% CV — ADF because a more-negative statistic
        // rejects the unit root (CV is −2.862), KPSS because a small η fails to reject the
        // stationarity null (CV is 0.463).
        return new StationarityTestResult(test, stat, cv, stat < cv[0.05], lags, n);
    }
}
