namespace TradingTerminal.Core.Quant;

/// <summary>
/// Result of a rolling Kyle-lambda regression r = λ·Δ + ε over a window.
/// </summary>
/// <param name="Lambda">λ̂: estimated price impact (slope of return on signed order flow).</param>
/// <param name="Intercept">Fitted intercept (drift not explained by flow).</param>
/// <param name="RSquared">R² of the fit.</param>
/// <param name="Residuals">ε series, aligned oldest → newest to the window.</param>
/// <param name="CumulativeResidual">ε_cum = Σ ε over the window (net unexplained move).</param>
/// <param name="CumulativeResidualZ">
/// z-score of <see cref="CumulativeResidual"/> against the rolling distribution of per-step
/// cumulative residuals within the window (0 if the window is too short or degenerate).
/// </param>
/// <param name="Count">Window length.</param>
public sealed record KyleResidualResult(
    double Lambda,
    double Intercept,
    double RSquared,
    double[] Residuals,
    double CumulativeResidual,
    double CumulativeResidualZ,
    int Count);

/// <summary>
/// Kyle's lambda via rolling OLS: regress per-step returns on signed order flow,
/// <c>rᵢ = λ·Δᵢ + εᵢ</c>. λ̂ is the price-impact coefficient (Kyle 1985); the residual ε is the
/// portion of the move <em>not</em> explained by contemporaneous flow, and a large cumulative
/// residual ε_cum signals price drift unsupported by order flow (a fade / reversion setup). All
/// pure, two-pass-stable, with σ→0 and small-n guards.
/// </summary>
public static class KyleResidual
{
    /// <summary>
    /// Fits <c>r = λ·Δ + ε</c> by OLS over the supplied window. <paramref name="returns"/> and
    /// <paramref name="signedFlow"/> must be the same length (ordered oldest → newest).
    /// The z-score compares the final ε_cum to the mean/σ of the running cumulative-residual path.
    /// </summary>
    public static KyleResidualResult Fit(IReadOnlyList<double> returns, IReadOnlyList<double> signedFlow)
    {
        ArgumentNullException.ThrowIfNull(returns);
        ArgumentNullException.ThrowIfNull(signedFlow);
        if (returns.Count != signedFlow.Count) throw new ArgumentException("returns and signedFlow must have equal length.");
        var n = returns.Count;
        if (n < 2) return new KyleResidualResult(0, 0, 0, new double[n], 0, 0, n);

        // OLS slope/intercept.
        double sx = 0, sy = 0;
        for (var i = 0; i < n; i++) { sx += signedFlow[i]; sy += returns[i]; }
        var mx = sx / n;
        var my = sy / n;
        double sxx = 0, sxy = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            var dx = signedFlow[i] - mx;
            var dy = returns[i] - my;
            sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
        }
        var lambda = sxx > 1e-300 ? sxy / sxx : 0.0;
        var intercept = my - lambda * mx;

        var eps = new double[n];
        double cum = 0, sres = 0;
        // Running cumulative-residual path, for the z-score.
        var cumPath = new double[n];
        for (var i = 0; i < n; i++)
        {
            var e = returns[i] - (intercept + lambda * signedFlow[i]);
            eps[i] = e;
            sres += e * e;
            cum += e;
            cumPath[i] = cum;
        }
        var r2 = syy > 1e-300 ? Math.Clamp(1.0 - sres / syy, 0.0, 1.0) : 0.0;

        // z-score of the final ε_cum against the distribution of the cumulative path.
        double mc = 0;
        for (var i = 0; i < n; i++) mc += cumPath[i];
        mc /= n;
        double vc = 0;
        for (var i = 0; i < n; i++) { var d = cumPath[i] - mc; vc += d * d; }
        var sdc = Math.Sqrt(vc / n);
        var z = sdc > 1e-12 ? (cum - mc) / sdc : 0.0;

        return new KyleResidualResult(lambda, intercept, r2, eps, cum, z, n);
    }
}
