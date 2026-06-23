namespace TradingTerminal.Core.Quant;

/// <summary>
/// Newey-West HAC (heteroskedasticity- and autocorrelation-consistent) result for a regression
/// slope.
/// </summary>
/// <param name="StandardError">HAC standard error of the slope β.</param>
/// <param name="TStat">β / SE (caller supplies β; 0 if SE is non-positive).</param>
/// <param name="Lag">Bandwidth L (number of lags) used.</param>
public sealed record NeweyWestResult(double StandardError, double TStat, int Lag);

/// <summary>
/// Newey-West (1987) HAC standard error for a simple-regression slope. Corrects the OLS slope SE
/// for serial correlation and heteroskedasticity in the residuals using Bartlett-kernel weights.
///
/// <para>For a univariate regression yᵢ = α + β xᵢ + εᵢ with residuals eᵢ and centred regressor
/// x̃ᵢ = xᵢ − x̄, let S_xx = Σ x̃ᵢ². The HAC "meat" is</para>
/// <code>
///   Ŝ = Σ x̃ᵢ²eᵢ²  +  Σ_{l=1..L} k_l · Σ_i x̃ᵢ x̃_{i−l} e_i e_{i−l} · 2
///   k_l = 1 − l/(L+1)                                   (Bartlett kernel)
///   Var(β̂) = Ŝ / S_xx²        ,        SE(β̂) = sqrt(Var)
/// </code>
/// <para>Default bandwidth follows the Newey-West (1994) plug-in rule
/// L = floor(4·(n/100)^(2/9)).</para>
/// </summary>
public static class NeweyWest
{
    /// <summary>Default automatic bandwidth L = floor(4·(n/100)^(2/9)), at least 0.</summary>
    public static int AutoLag(int n) => n <= 1 ? 0 : Math.Max(0, (int)Math.Floor(4.0 * Math.Pow(n / 100.0, 2.0 / 9.0)));

    /// <summary>
    /// HAC standard error of the slope given the regressor series <paramref name="x"/> and the
    /// regression residuals <paramref name="residuals"/> (same length, ordered consistently).
    /// <paramref name="lag"/> &lt; 0 selects <see cref="AutoLag"/>. <paramref name="slope"/> is only
    /// used to populate the t-stat.
    /// </summary>
    public static NeweyWestResult SlopeStandardError(
        IReadOnlyList<double> x,
        IReadOnlyList<double> residuals,
        double slope = 0.0,
        int lag = -1)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(residuals);
        if (x.Count != residuals.Count) throw new ArgumentException("x and residuals must have equal length.");
        var n = x.Count;
        if (n < 3) return new NeweyWestResult(0, 0, 0);

        var L = lag < 0 ? AutoLag(n) : lag;
        if (L >= n) L = n - 1;

        // Centred regressor.
        double mx = 0;
        for (var i = 0; i < n; i++) mx += x[i];
        mx /= n;
        var xt = new double[n];
        double sxx = 0;
        for (var i = 0; i < n; i++) { xt[i] = x[i] - mx; sxx += xt[i] * xt[i]; }
        if (sxx <= 1e-300) return new NeweyWestResult(0, 0, L);

        // Score uᵢ = x̃ᵢ·eᵢ.
        var u = new double[n];
        for (var i = 0; i < n; i++) u[i] = xt[i] * residuals[i];

        // Lag-0 autocovariance.
        double s = 0;
        for (var i = 0; i < n; i++) s += u[i] * u[i];

        // Bartlett-weighted lagged autocovariances.
        for (var l = 1; l <= L; l++)
        {
            double gamma = 0;
            for (var i = l; i < n; i++) gamma += u[i] * u[i - l];
            var k = 1.0 - (double)l / (L + 1);
            s += 2.0 * k * gamma;
        }

        var variance = s / (sxx * sxx);
        var se = variance > 0 ? Math.Sqrt(variance) : 0.0;
        var t = se > 1e-300 ? slope / se : 0.0;
        return new NeweyWestResult(se, t, L);
    }
}
