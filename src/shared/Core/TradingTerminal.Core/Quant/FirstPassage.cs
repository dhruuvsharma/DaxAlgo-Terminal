namespace TradingTerminal.Core.Quant;

/// <summary>
/// First-passage win probability and expected value for a drifted Brownian price with a fixed
/// stop and target (a two-barrier / "gambler's ruin with drift" problem). For a process
/// dX = μ dt + σ dW started at 0, with an upper barrier at +b (target) and lower barrier at −a
/// (stop), the probability of hitting +b before −a is
/// <code>
///   P = (e^{2μa/σ²} − 1) / (e^{2μa/σ²} − e^{−2μb/σ²})
/// </code>
/// In the driftless limit μ → 0 this reduces analytically to <c>P = a / (a + b)</c>. The EV helper
/// applies the asymmetric payoff and round-trip costs.
/// </summary>
public static class FirstPassage
{
    /// <summary>
    /// Win probability P(hit +b before −a), optionally penalised for <b>jump / gap risk</b>.
    /// <paramref name="stop"/> = a &gt; 0 (distance below entry), <paramref name="target"/> = b &gt; 0
    /// (distance above entry), drift <paramref name="mu"/>, vol <paramref name="sigma"/> (&gt; 0).
    /// Handles μ→0 analytically and guards overflow by working in the exponent space.
    ///
    /// <para>The closed form assumes a <em>continuous</em> price path, so it ignores the chance a
    /// single bar gaps straight through the stop (a discontinuity the Brownian model can't see).
    /// <paramref name="gapPenalty"/> is the caller-supplied probability mass of such an adverse gap
    /// (e.g. the empirical frequency of recent bar ranges large enough to span the stop). It is
    /// deducted from the continuous win probability: <c>P_adj = clamp(P − gapPenalty, 0, 1)</c>.</para>
    ///
    /// Returns a value in [0, 1].
    /// </summary>
    public static double WinProbability(double stop, double target, double mu, double sigma, double gapPenalty = 0.0)
    {
        var p = WinProbabilityContinuous(stop, target, mu, sigma);
        if (gapPenalty > 0.0) p = Math.Clamp(p - gapPenalty, 0.0, 1.0);
        return p;
    }

    /// <summary>The pure continuous-path first-passage probability (no gap adjustment).</summary>
    private static double WinProbabilityContinuous(double stop, double target, double mu, double sigma)
    {
        var a = stop;
        var b = target;
        if (a <= 0 && b <= 0) return 0.5;
        if (a <= 0) return 0.0; // zero room to the stop ⇒ stopped immediately
        if (b <= 0) return 1.0; // zero room to the target ⇒ target immediately
        if (sigma <= 0) return mu >= 0 ? 1.0 : 0.0; // deterministic drift

        var theta = 2.0 * mu / (sigma * sigma);
        if (Math.Abs(theta) < 1e-12) return a / (a + b); // driftless limit

        // P = (e^{θa} − 1) / (e^{θa} − e^{−θb}). Rewrite to avoid overflow in *either* tail by
        // dividing through by whichever exponential dominates, so every Exp argument stays ≤ 0.
        double num, den;
        if (theta > 0)
        {
            // θ > 0: divide by e^{θa} ⇒ P = (1 − e^{−θa}) / (1 − e^{−θ(a+b)}). Args ≤ 0.
            num = 1.0 - Math.Exp(-theta * a);
            den = 1.0 - Math.Exp(-theta * (a + b));
        }
        else
        {
            // θ < 0: divide by e^{−θb} ⇒ P = (e^{θ(a+b)} − e^{θb}) / (e^{θ(a+b)} − 1). Args ≤ 0.
            num = Math.Exp(theta * (a + b)) - Math.Exp(theta * b);
            den = Math.Exp(theta * (a + b)) - 1.0;
        }
        if (Math.Abs(den) < 1e-300) return a / (a + b);
        return Math.Clamp(num / den, 0.0, 1.0);
    }

    /// <summary>
    /// Expected value of the trade in price units:
    /// <c>EV = P·b − (1−P)·a − roundTripCosts</c>. <paramref name="winProbability"/> is typically
    /// the output of <see cref="WinProbability"/>; <paramref name="roundTripCosts"/> bundles
    /// spread + commission + expected slippage, both legs.
    /// </summary>
    public static double ExpectedValue(double winProbability, double target, double stop, double roundTripCosts = 0.0)
    {
        var p = Math.Clamp(winProbability, 0.0, 1.0);
        return p * target - (1.0 - p) * stop - roundTripCosts;
    }
}
