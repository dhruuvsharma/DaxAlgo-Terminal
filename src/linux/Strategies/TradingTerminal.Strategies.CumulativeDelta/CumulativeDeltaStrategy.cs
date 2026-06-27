using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.CumulativeDelta;

public sealed class CumulativeDeltaStrategy : ITradingStrategy
{
    public string Id => "cumulative.delta.scalper";
    public string DisplayName => "Cumulative Delta Scalper";
    public string Description =>
        "Sniper-mode order-flow delta scalper. Trade-tape aggressor delta (with footprint clusters) — " +
        "bid-tick proxy when no tape — summed over a sliding bar window; trigger on cumΔ crossing an " +
        "adaptive ±σ threshold, gated by up to 6 confirmations (momentum, HTF EMA, EMA slope, ADX, " +
        "spread, footprint stack). Basis-point spread/ATR gates work on any instrument. " +
        "Display only — does not place orders.";

    /// <summary>
    /// Requires L1 quotes and OHLCV bars; consumes the trade tape when the broker provides one
    /// (IB) for true aggressor-volume delta + footprints, degrading to the bid-tick proxy otherwise.
    /// </summary>
    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.TradeTape;
}
