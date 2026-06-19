namespace TradingTerminal.Core.Quant;

/// <summary>
/// Result of a rolling Kyle-lambda regression r = λ·Δ + ε over a window, estimated by
/// <b>two-stage least squares (2SLS)</b> with lagged flow as the instrument.
/// </summary>
/// <param name="Lambda">λ̂: estimated price impact (IV slope of return on signed order flow).</param>
/// <param name="Intercept">Fitted intercept (drift not explained by flow).</param>
/// <param name="RSquared">Structural R² of the fit (using actual flow, not the fitted instrument).</param>
/// <param name="Residuals">ε series, aligned oldest → newest to the (lag-trimmed) window.</param>
/// <param name="CumulativeResidual">ε_cum = Σ ε over the window (net unexplained move).</param>
/// <param name="CumulativeResidualZ">
/// z-score of <see cref="CumulativeResidual"/> against the rolling distribution of per-step
/// cumulative residuals within the window (0 if the window is too short or degenerate).
/// </param>
/// <param name="Count">Effective sample size after the one-bar lag trim (n − 1).</param>
/// <param name="FirstStageRSquared">
/// First-stage R² of Δ_t on Δ_{t−1}. This is the <b>instrument-strength</b> diagnostic: a low value
/// (a "weak instrument") means the lagged flow barely predicts current flow, so λ̂ is poorly
/// identified and should be trusted less. Callers can fold this into the signal confidence.
/// </param>
public sealed record KyleResidualResult(
    double Lambda,
    double Intercept,
    double RSquared,
    double[] Residuals,
    double CumulativeResidual,
    double CumulativeResidualZ,
    int Count,
    double FirstStageRSquared);

/// <summary>
/// Kyle's lambda via <b>two-stage least squares (2SLS) instrumental-variable</b> regression.
///
/// <para><b>Why not plain OLS.</b> A naive contemporaneous OLS of return on signed flow,
/// <c>rᵢ = λ·Δᵢ + εᵢ</c>, is <em>biased</em> whenever the regressor Δ is correlated with the error ε
/// — the classic simultaneity/endogeneity problem of microstructure: price moves and aggressor flow
/// are jointly determined within a bar, and bar-aggregation measurement error contaminates Δ. OLS
/// then conflates genuine price impact with that contemporaneous co-movement, over-stating λ̂.</para>
///
/// <para><b>The 2SLS fix.</b> We instrument the endogenous regressor Δ_t with its own one-bar lag
/// Δ_{t−1}. A valid instrument must be (i) <em>relevant</em> — correlated with Δ_t, which holds when
/// order flow is autocorrelated (it persists: large buy bars cluster), and (ii) <em>exogenous</em> —
/// uncorrelated with the current structural error ε_t, which holds because last bar's flow is
/// predetermined relative to this bar's idiosyncratic price shock.</para>
/// <list type="number">
///   <item><b>Stage 1:</b> regress Δ_t on Δ_{t−1} (with intercept) and take the fitted Δ̂_t. This
///   projects out only the part of current flow that was predictable from past flow — the part that
///   cannot be contaminated by the current price shock.</item>
///   <item><b>Stage 2:</b> regress r_t on Δ̂_t (with intercept). The slope is the IV estimate λ̂.</item>
/// </list>
/// The structural residual is then ε_t = r_t − (α + λ̂·Δ_t) using the <em>actual</em> flow (the
/// standard IV residual), and ε_cum / its z-score flag price drift unexplained by flow exactly as
/// before. All pure, two-pass-stable, with σ→0 and small-n guards.
/// </summary>
public static class KyleResidual
{
    /// <summary>
    /// Fits <c>r = λ·Δ + ε</c> by 2SLS over the supplied window, instrumenting Δ_t with Δ_{t−1}.
    /// <paramref name="returns"/> and <paramref name="signedFlow"/> must be the same length (ordered
    /// oldest → newest). Needs at least 3 observations (one is consumed by the lag).
    /// </summary>
    public static KyleResidualResult Fit(IReadOnlyList<double> returns, IReadOnlyList<double> signedFlow)
    {
        ArgumentNullException.ThrowIfNull(returns);
        ArgumentNullException.ThrowIfNull(signedFlow);
        if (returns.Count != signedFlow.Count) throw new ArgumentException("returns and signedFlow must have equal length.");
        var rawN = returns.Count;

        // One observation is consumed building the lagged instrument, so the effective sample is
        // m = rawN − 1. Below 2 usable rows the regression is degenerate.
        var m = rawN - 1;
        if (m < 2) return new KyleResidualResult(0, 0, 0, new double[Math.Max(0, m)], 0, 0, Math.Max(0, m), 0);

        // Aligned IV arrays (length m), all ordered oldest → newest:
        //   y[k] = r_{k+1}      (current return)
        //   x[k] = Δ_{k+1}      (current, endogenous flow)
        //   z[k] = Δ_{k}        (lagged flow — the instrument)
        var y = new double[m];
        var x = new double[m];
        var z = new double[m];
        for (var k = 0; k < m; k++)
        {
            y[k] = returns[k + 1];
            x[k] = signedFlow[k + 1];
            z[k] = signedFlow[k];
        }

        // ── Stage 1: x ~ z  ⇒  x̂ = a1 + b1·z. Also yields the first-stage R² (instrument strength).
        var (b1, a1, firstStageR2) = OlsWithR2(z, x);
        var xhat = new double[m];
        for (var k = 0; k < m; k++) xhat[k] = a1 + b1 * z[k];

        // ── Stage 2: y ~ x̂  ⇒  λ̂ = slope of return on the *predicted* (exogenous) flow.
        var (lambda, _, _) = OlsWithR2(xhat, y);

        // Structural intercept uses the actual means (standard IV): α = ȳ − λ̂·x̄.
        double mx = 0, my = 0;
        for (var k = 0; k < m; k++) { mx += x[k]; my += y[k]; }
        mx /= m; my /= m;
        var intercept = my - lambda * mx;

        // Structural residuals against the ACTUAL flow (the IV residual), plus the running path.
        var eps = new double[m];
        double cum = 0, sres = 0, syy = 0;
        var cumPath = new double[m];
        for (var k = 0; k < m; k++)
        {
            var e = y[k] - (intercept + lambda * x[k]);
            eps[k] = e;
            sres += e * e;
            var dy = y[k] - my;
            syy += dy * dy;
            cum += e;
            cumPath[k] = cum;
        }
        var r2 = syy > 1e-300 ? Math.Clamp(1.0 - sres / syy, 0.0, 1.0) : 0.0;

        // z-score of the final ε_cum against the distribution of the cumulative path.
        double mc = 0;
        for (var k = 0; k < m; k++) mc += cumPath[k];
        mc /= m;
        double vc = 0;
        for (var k = 0; k < m; k++) { var d = cumPath[k] - mc; vc += d * d; }
        var sdc = Math.Sqrt(vc / m);
        var zScore = sdc > 1e-12 ? (cum - mc) / sdc : 0.0;

        return new KyleResidualResult(lambda, intercept, r2, eps, cum, zScore, m, firstStageR2);
    }

    /// <summary>
    /// Plain OLS of <paramref name="yv"/> on <paramref name="xv"/> (equal length). Returns the slope,
    /// intercept, and R². Two-pass-stable; slope 0 when the regressor has no variance.
    /// </summary>
    private static (double Slope, double Intercept, double RSquared) OlsWithR2(double[] xv, double[] yv)
    {
        var n = xv.Length;
        if (n < 2) return (0, n == 1 ? yv[0] : 0, 0);
        double sx = 0, sy = 0;
        for (var i = 0; i < n; i++) { sx += xv[i]; sy += yv[i]; }
        var mx = sx / n; var my = sy / n;
        double sxx = 0, sxy = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            var dx = xv[i] - mx; var dy = yv[i] - my;
            sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
        }
        var slope = sxx > 1e-300 ? sxy / sxx : 0.0;
        var intercept = my - slope * mx;
        // R² = (Σ dx·dy)² / (Σ dx² · Σ dy²) for a simple regression.
        var r2 = sxx > 1e-300 && syy > 1e-300 ? Math.Clamp(sxy * sxy / (sxx * syy), 0.0, 1.0) : 0.0;
        return (slope, intercept, r2);
    }
}
