namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Order-flow / order-book imbalance (OBI) primitives from Anantha, Jain &amp; Maiti (2025),
/// "Order-Flow Filtration and Directional Association with Short-Horizon Returns"
/// (arXiv:2507.22712). All functions are pure (no allocations, no state) and live in Core so
/// the engine-side strategy and the live view-model share one definition.
///
/// <para>The paper studies two imbalance flavours over a backward window (τ−h, τ]:</para>
/// <list type="bullet">
///   <item><b>Order-based OBI</b> — net directional event counts from the standing book.</item>
///   <item><b>Trade-based OBI(T)</b> — net <em>signed-trade</em> count from executed trades
///   (their Eq. 17): <c>(N_buy − N_sell) / (N_buy + N_sell)</c>. This is the flavour our
///   signed-tape feeds can reproduce, and the one the paper finds carries the cleaner
///   directional imprint once short-lived / heavily-revised parent orders are filtered out.</item>
/// </list>
/// </summary>
public static class OrderFlowImbalance
{
    /// <summary>Number of discrete OBI regimes (Section 3.4): nine ordered bins from strongly
    /// negative through a central neutral band to strongly positive, indices −4..+4.</summary>
    public const int RegimeCount = 9;

    /// <summary>
    /// Trade-based imbalance over a window: <c>(buy − sell) / (buy + sell)</c>, in [−1, 1].
    /// Positive ⇒ buyer-initiated pressure dominates (bullish lean); negative ⇒ seller-initiated.
    /// Returns 0 when the window holds no classified trades (the paper excludes such windows).
    /// </summary>
    public static double TradeImbalance(long buyCount, long sellCount)
    {
        var total = buyCount + sellCount;
        if (total <= 0) return 0;
        return (double)(buyCount - sellCount) / total;
    }

    /// <summary>
    /// Classifies an OBI value in [−1, 1] into one of <see cref="RegimeCount"/> symmetric regimes,
    /// uniformly spaced across the support with a central neutral band (Section 3.4 modeling
    /// remark). Returns a signed index in [−4, +4]: −4 = strongly negative, 0 = neutral,
    /// +4 = strongly positive. Out-of-range inputs are clamped.
    /// </summary>
    public static int Regime(double obi)
    {
        if (double.IsNaN(obi)) return 0;
        var x = Math.Clamp(obi, -1.0, 1.0);

        // Nine equal-width bins across [−1, 1]; bin index 0..8, recentred to −4..+4.
        const double binWidth = 2.0 / RegimeCount;
        var bin = (int)Math.Floor((x + 1.0) / binWidth);
        bin = Math.Clamp(bin, 0, RegimeCount - 1);
        return bin - RegimeCount / 2;
    }

    /// <summary>
    /// True when an OBI value sits in a "strong" regime, i.e. its signed regime index magnitude
    /// is at least <paramref name="strongRegime"/>. The directional signal fires from these
    /// regimes (the paper shows strong OBI regimes excite same-sign return regimes).
    /// </summary>
    public static bool IsStrong(double obi, int strongRegime) =>
        Math.Abs(Regime(obi)) >= Math.Max(1, strongRegime);

    /// <summary>Short human label for a signed regime index, e.g. "+3" / "0" / "−4".</summary>
    public static string RegimeLabel(int regime) =>
        regime == 0 ? "0" : regime > 0 ? $"+{regime}" : regime.ToString();
}
