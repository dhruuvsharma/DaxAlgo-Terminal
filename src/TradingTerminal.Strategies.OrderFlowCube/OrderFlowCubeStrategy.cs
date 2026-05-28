using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OrderFlowCube;

public sealed class OrderFlowCubeStrategy : ITradingStrategy
{
    public string Id => "orderflow.cube";
    public string DisplayName => "Order Flow Cube";
    public string Description => "Phase-space view of order flow: CVD imbalance (trend window) vs aggressor ratio (recent window) with size-ratio markers. Detects institutional accumulation/distribution regimes from the trade tape.";
}
