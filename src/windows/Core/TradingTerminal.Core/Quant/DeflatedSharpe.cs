namespace TradingTerminal.Core.Quant;

/// <summary>
/// Deflated Sharpe ratio result.
/// </summary>
/// <param name="Dsr">DSR ∈ [0, 1]: probability the true SR exceeds the expected max of N trials.</param>
/// <param name="ExpectedMaxSharpe">E[max SR] under the null (the deflation benchmark, SR₀).</param>
/// <param name="Trials">N: number of independent strategy configurations tried.</param>
public sealed record DeflatedSharpeResult(double Dsr, double ExpectedMaxSharpe, int Trials);

/// <summary>
/// Bailey &amp; López de Prado (2014) deflated Sharpe ratio (DSR). When you try N strategy
/// configurations and keep the best, its observed Sharpe is upward-biased by selection. DSR is the
/// probability that the strategy's <em>true</em> Sharpe is positive after deflating by the Sharpe
/// you'd expect from the best of N pure-noise trials, while also correcting for non-normal
/// (skewed, fat-tailed) returns and finite sample length.
///
/// <para>Steps:</para>
/// <list type="number">
/// <item>Expected max Sharpe of N trials (per-period units):
/// <c>SR₀ = sqrt(Var) · [ (1−γ)·Z⁻¹(1 − 1/N) + γ·Z⁻¹(1 − 1/(N·e)) ]</c>, where γ is the
/// Euler-Mascheroni constant, Var is the variance of the trial Sharpe ratios, and Z⁻¹ is the
/// standard-normal quantile.</item>
/// <item>DSR = Φ( (SR̂ − SR₀)·sqrt(T−1) / sqrt(1 − skew·SR̂ + (kurt−1)/4·SR̂²) ), with SR̂ the
/// observed (per-period) Sharpe, T the sample length, skew/kurt the return moments (kurt is the
/// non-excess fourth standardised moment, = 3 for normal).</item>
/// </list>
/// </summary>
public static class DeflatedSharpe
{
    private const double EulerMascheroni = 0.5772156649015329;

    /// <summary>
    /// Computes the DSR. <paramref name="observedSharpe"/> SR̂ and <paramref name="trialSharpeVariance"/>
    /// must be in the same (per-period) units. <paramref name="skew"/> and <paramref name="kurtosis"/>
    /// are the return distribution's skewness and (non-excess) kurtosis; <paramref name="sampleLength"/>
    /// is T (number of return observations); <paramref name="trials"/> is N.
    /// </summary>
    public static DeflatedSharpeResult Compute(
        double observedSharpe,
        int trials,
        double trialSharpeVariance,
        double skew,
        double kurtosis,
        int sampleLength)
    {
        if (trials < 1) trials = 1;
        var n = trials;
        var sd = Math.Sqrt(Math.Max(0, trialSharpeVariance));

        // Expected max Sharpe of N trials.
        double sr0;
        if (n == 1 || sd <= 1e-300)
        {
            sr0 = 0.0; // single trial ⇒ no selection bias to deflate.
        }
        else
        {
            var z1 = NormInv(1.0 - 1.0 / n);
            var z2 = NormInv(1.0 - 1.0 / (n * Math.E));
            sr0 = sd * ((1.0 - EulerMascheroni) * z1 + EulerMascheroni * z2);
        }

        double dsr;
        if (sampleLength < 2)
        {
            dsr = double.NaN;
        }
        else
        {
            var denomVar = 1.0 - skew * observedSharpe + (kurtosis - 1.0) / 4.0 * observedSharpe * observedSharpe;
            denomVar = Math.Max(denomVar, 1e-12);
            var z = (observedSharpe - sr0) * Math.Sqrt(sampleLength - 1.0) / Math.Sqrt(denomVar);
            dsr = NormCdf(z);
        }

        return new DeflatedSharpeResult(dsr, sr0, n);
    }

    /// <summary>Standard-normal CDF Φ(x) via the complementary error function.</summary>
    public static double NormCdf(double x) => 0.5 * Erfc(-x / Math.Sqrt(2.0));

    /// <summary>
    /// Standard-normal inverse CDF (quantile) Z⁻¹(p), Acklam's rational approximation,
    /// accurate to ~1e-9 across p ∈ (0, 1).
    /// </summary>
    public static double NormInv(double p)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;

        // Acklam coefficients.
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };

        const double pLow = 0.02425;
        const double pHigh = 1 - pLow;
        double q, r;
        if (p < pLow)
        {
            q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                   ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p <= pHigh)
        {
            q = p - 0.5; r = q * q;
            return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q /
                   (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
        }
        q = Math.Sqrt(-2 * Math.Log(1 - p));
        return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
    }

    /// <summary>Complementary error function via the Numerical-Recipes erfcc approximation (~1e-7).</summary>
    private static double Erfc(double x)
    {
        var z = Math.Abs(x);
        var t = 1.0 / (1.0 + 0.5 * z);
        var ans = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 +
            t * (0.09678418 + t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 +
            t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? ans : 2.0 - ans;
    }
}
