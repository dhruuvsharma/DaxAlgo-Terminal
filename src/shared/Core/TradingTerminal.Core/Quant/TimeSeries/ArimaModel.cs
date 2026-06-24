namespace TradingTerminal.Core.Quant.TimeSeries;

/// <summary>A fitted ARIMA(p,d,q) model over a (log-)price series.</summary>
public sealed record ArimaFit(
    int P, int D, int Q,
    double Constant,
    double[] ArCoefficients,
    double[] MaCoefficients,
    double SigmaSquared,
    double Aic,
    double Bic,
    int N,
    double[] Residuals);

/// <summary>One forecast step: point forecast plus a symmetric 95% interval, in input units.</summary>
public sealed record ArimaForecastPoint(double Mean, double Lower95, double Upper95);

/// <summary>
/// ARIMA(p,d,q) by the Hannan-Rissanen two-stage regression — all OLS, no nonlinear optimizer,
/// which keeps the fit deterministic and fast enough to run on every button press:
/// <list type="number">
/// <item>Difference the input d times.</item>
/// <item>Stage 1: long autoregression AR(m), m = max(p+q+3, ⌊∜n⌋·2), to proxy the innovations εₜ.</item>
/// <item>Stage 2: OLS of yₜ on [1, yₜ₋₁..yₜ₋p, ε̂ₜ₋₁..ε̂ₜ₋q] → (c, φ, θ).</item>
/// </list>
/// Forecasts iterate the recursion with future innovations at 0; interval widths come from the
/// ψ-weight expansion, cumulated through the d integrations so price-level bands widen correctly.
/// AIC/BIC use the Gaussian likelihood approximation n·ln σ̂² + penalty — comparable across
/// (p,q) at fixed d, which is what the order-search table needs.
/// </summary>
public static class ArimaModel
{
    public const int MaxOrder = 5;

    /// <summary>Minimum observations (after differencing) for a sane fit.</summary>
    public static int MinObservations(int p, int q) => Math.Max(40, 6 * (p + q + 1));

    /// <summary>
    /// Fits ARIMA(p,d,q) to <paramref name="series"/> (typically log prices). Returns null when
    /// the sample is too short or the regressions are degenerate.
    /// </summary>
    public static ArimaFit? Fit(IReadOnlyList<double> series, int p, int d, int q)
    {
        if (p < 0 || q < 0 || d < 0 || p > MaxOrder || q > MaxOrder || d > 2) return null;

        var y = Difference(series, d);
        var n = y.Length;
        if (n < MinObservations(p, q)) return null;

        // Stage 1: innovations proxy from a long AR (only needed when q > 0).
        double[] eps;
        if (q > 0)
        {
            var m = Math.Min(Math.Max(p + q + 3, 2 * (int)Math.Pow(n, 0.25)), n / 4);
            var arFit = FitAr(y, m);
            if (arFit is null) return null;
            eps = new double[n];
            // Residuals start at index m; earlier slots stay 0 (their rows are excluded below).
            for (var i = 0; i < arFit.Residuals.Length; i++) eps[m + i] = arFit.Residuals[i];
        }
        else
        {
            eps = new double[n];
        }

        // Stage 2: regression with AR and MA terms.
        var start = Math.Max(p, q == 0 ? p : Math.Max(q, Math.Min(Math.Max(p + q + 3, 2 * (int)Math.Pow(n, 0.25)), n / 4)));
        var rows = n - start;
        var k = 1 + p + q;
        if (rows <= k + 2) return null;

        var x = new double[rows][];
        var target = new double[rows];
        for (var i = 0; i < rows; i++)
        {
            var t = start + i;
            target[i] = y[t];
            var row = new double[k];
            row[0] = 1.0;
            for (var j = 1; j <= p; j++) row[j] = y[t - j];
            for (var j = 1; j <= q; j++) row[p + j] = eps[t - j];
            x[i] = row;
        }

        var fit = Ols.Fit(x, target);
        if (fit is null) return null;

        var ar = new double[p];
        var ma = new double[q];
        for (var j = 0; j < p; j++) ar[j] = fit.Beta[1 + j];
        for (var j = 0; j < q; j++) ma[j] = fit.Beta[1 + p + j];

        var sigma2 = Math.Max(fit.Rss / rows, 1e-300);
        var paramCount = k + 1; // + variance
        var aic = rows * Math.Log(sigma2) + 2.0 * paramCount;
        var bic = rows * Math.Log(sigma2) + paramCount * Math.Log(rows);

        return new ArimaFit(p, d, q, fit.Beta[0], ar, ma, sigma2, aic, bic, rows, fit.Residuals);
    }

    /// <summary>
    /// h-step-ahead forecast in the ORIGINAL (undifferenced) units of the fitted series.
    /// <paramref name="series"/> must be the same series passed to <see cref="Fit"/>.
    /// </summary>
    public static ArimaForecastPoint[] Forecast(ArimaFit fit, IReadOnlyList<double> series, int horizon)
    {
        if (horizon <= 0) return Array.Empty<ArimaForecastPoint>();

        var y = Difference(series, fit.D);
        var n = y.Length;
        var p = fit.P;
        var q = fit.Q;

        // Recent values + residuals to seed the recursion. Residuals from the fit align to the
        // END of the differenced sample.
        var hist = new List<double>(y);
        var resid = new double[n];
        var rOff = n - fit.Residuals.Length;
        for (var i = 0; i < fit.Residuals.Length; i++)
            if (rOff + i >= 0) resid[rOff + i] = fit.Residuals[i];
        var residList = new List<double>(resid);

        var diffForecast = new double[horizon];
        for (var h = 0; h < horizon; h++)
        {
            var t = hist.Count;
            var f = fit.Constant;
            for (var j = 1; j <= p; j++) f += fit.ArCoefficients[j - 1] * hist[t - j];
            for (var j = 1; j <= q; j++)
            {
                var idx = t - j;
                if (idx < residList.Count) f += fit.MaCoefficients[j - 1] * residList[idx];
                // future innovations are 0 in expectation
            }
            diffForecast[h] = f;
            hist.Add(f);
            residList.Add(0.0);
        }

        // ψ-weights of the ARMA part, then integrate d times (cumulate) so the variance of the
        // level forecast accumulates correctly.
        var psi = PsiWeights(fit.ArCoefficients, fit.MaCoefficients, horizon);
        var psiLevel = (double[])psi.Clone();
        for (var pass = 0; pass < fit.D; pass++)
        {
            for (var i = 1; i < horizon; i++) psiLevel[i] += psiLevel[i - 1];
        }

        // Integrate the point forecast back to levels.
        var levelForecast = (double[])diffForecast.Clone();
        var anchor = series[^1];
        if (fit.D > 0)
        {
            // For d=1: level_h = anchor + Σ diff. For d=2: integrate twice using the last first-difference.
            if (fit.D == 1)
            {
                double acc = anchor;
                for (var h = 0; h < horizon; h++) { acc += diffForecast[h]; levelForecast[h] = acc; }
            }
            else
            {
                var lastDiff = series.Count >= 2 ? series[^1] - series[^2] : 0.0;
                double dAcc = lastDiff, acc = anchor;
                for (var h = 0; h < horizon; h++)
                {
                    dAcc += diffForecast[h];
                    acc += dAcc;
                    levelForecast[h] = acc;
                }
            }
        }

        var points = new ArimaForecastPoint[horizon];
        double cumVar = 0;
        for (var h = 0; h < horizon; h++)
        {
            cumVar += psiLevel[h] * psiLevel[h];
            var se = Math.Sqrt(Math.Max(fit.SigmaSquared * cumVar, 0));
            points[h] = new ArimaForecastPoint(levelForecast[h], levelForecast[h] - 1.96 * se, levelForecast[h] + 1.96 * se);
        }
        return points;
    }

    /// <summary>ψ₀..ψ_{h−1} from ψ_j = θ_j + Σ_{i≤min(j,p)} φ_i·ψ_{j−i} (ψ₀ = 1, θ_j = 0 past q).</summary>
    internal static double[] PsiWeights(double[] ar, double[] ma, int count)
    {
        var psi = new double[count];
        if (count == 0) return psi;
        psi[0] = 1.0;
        for (var j = 1; j < count; j++)
        {
            var v = j <= ma.Length ? ma[j - 1] : 0.0;
            for (var i = 1; i <= Math.Min(j, ar.Length); i++) v += ar[i - 1] * psi[j - i];
            psi[j] = v;
        }
        return psi;
    }

    /// <summary>Plain AR(m) with constant, used as the Hannan-Rissanen first stage.</summary>
    private static OlsResult? FitAr(double[] y, int m)
    {
        var n = y.Length;
        var rows = n - m;
        if (rows <= m + 3) return null;

        var x = new double[rows][];
        var target = new double[rows];
        for (var i = 0; i < rows; i++)
        {
            var t = m + i;
            target[i] = y[t];
            var row = new double[m + 1];
            row[0] = 1.0;
            for (var j = 1; j <= m; j++) row[j] = y[t - j];
            x[i] = row;
        }
        return Ols.Fit(x, target);
    }

    internal static double[] Difference(IReadOnlyList<double> series, int d)
    {
        var cur = new double[series.Count];
        for (var i = 0; i < series.Count; i++) cur[i] = series[i];
        for (var pass = 0; pass < d; pass++)
        {
            if (cur.Length < 2) return Array.Empty<double>();
            var next = new double[cur.Length - 1];
            for (var i = 1; i < cur.Length; i++) next[i - 1] = cur[i] - cur[i - 1];
            cur = next;
        }
        return cur;
    }
}
