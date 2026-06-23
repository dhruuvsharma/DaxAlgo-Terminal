namespace TradingTerminal.Core.Quant;

/// <summary>
/// Combination-weight solver for blending k signals into one composite. The mean-variance optimal
/// (unconstrained, up to scale) weight vector is <c>w ∝ Σ⁻¹·IC</c>, where Σ is the signal
/// covariance/correlation matrix and IC is the vector of per-signal information coefficients
/// (their predictive correlation with forward return). This is the discrete analogue of the
/// Grinold-Kahn / Markowitz solution: it over-weights signals with high IC and down-weights those
/// that are redundant (highly correlated with others).
///
/// <para>Normalisation: weights are scaled so <c>Σ|wᵢ| = 1</c> (L1 normalisation). This keeps the
/// gross exposure fixed while <em>preserving sign</em>, so a signal whose IC implies it should be
/// faded keeps its negative (hedge) weight. Negative weights are allowed by design.</para>
/// </summary>
public static class SignalWeights
{
    /// <summary>
    /// Solves <c>w ∝ Σ⁻¹·ic</c> and L1-normalises (Σ|wᵢ| = 1). Uses
    /// <see cref="LedoitWolf.SafeInverse"/> so a near-singular Σ still yields a usable solution.
    /// When all weights vanish, falls back to equal-magnitude weights carrying each IC's sign.
    /// </summary>
    /// <param name="signalCovariance">k×k signal covariance (or correlation) matrix Σ.</param>
    /// <param name="informationCoefficients">Length-k IC vector.</param>
    public static double[] Solve(double[,] signalCovariance, IReadOnlyList<double> informationCoefficients)
    {
        ArgumentNullException.ThrowIfNull(signalCovariance);
        ArgumentNullException.ThrowIfNull(informationCoefficients);
        var k = informationCoefficients.Count;
        if (signalCovariance.GetLength(0) != k || signalCovariance.GetLength(1) != k)
            throw new ArgumentException("Covariance dimension must match IC length.");
        if (k == 0) return Array.Empty<double>();

        var inv = LedoitWolf.SafeInverse(signalCovariance);
        var w = new double[k];
        for (var i = 0; i < k; i++)
        {
            double acc = 0;
            for (var j = 0; j < k; j++) acc += inv[i, j] * informationCoefficients[j];
            w[i] = acc;
        }

        double l1 = 0;
        for (var i = 0; i < k; i++) l1 += Math.Abs(w[i]);
        if (l1 <= 1e-300)
        {
            // Degenerate: fall back to sign(IC) equal-magnitude.
            double s = 0;
            for (var i = 0; i < k; i++) s += Math.Abs(informationCoefficients[i]);
            if (s <= 1e-300) { for (var i = 0; i < k; i++) w[i] = 1.0 / k; return w; }
            for (var i = 0; i < k; i++) w[i] = informationCoefficients[i] / s;
            return w;
        }

        for (var i = 0; i < k; i++) w[i] /= l1;
        return w;
    }
}
