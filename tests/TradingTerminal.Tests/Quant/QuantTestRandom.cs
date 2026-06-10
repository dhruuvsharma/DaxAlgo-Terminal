namespace TradingTerminal.Tests.Quant;

/// <summary>
/// Deterministic Gaussian helper for the Core quant test suite. Box-Muller transform over a
/// seeded <see cref="Random"/> so every distribution-based assertion is reproducible.
/// </summary>
internal static class QuantTestRandom
{
    /// <summary>Standard-normal draw (mean 0, sd 1) via Box-Muller.</summary>
    public static double NextGaussian(this Random rng)
    {
        // Avoid log(0).
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
