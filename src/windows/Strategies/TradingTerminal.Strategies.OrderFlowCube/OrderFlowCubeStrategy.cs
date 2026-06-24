using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OrderFlowCube;

public sealed class OrderFlowCubeStrategy : ITradingStrategy
{
    public string Id => "orderflow.cube";
    public string DisplayName => "Order Flow Cube";
    public string Description => "Phase-space view of order flow: CVD imbalance (trend window) vs aggressor ratio (recent window) with size-ratio markers. Detects institutional accumulation/distribution regimes from the trade tape.";

    /// <summary>
    /// Requires L1 quotes, bars, and the trade tape: the cube axes are derived
    /// from per-trade signed volume / aggressor flow (engine-side
    /// <c>OnTradeAsync</c>), so this is IB-only.
    /// </summary>
    public StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.TradeTape;
}
