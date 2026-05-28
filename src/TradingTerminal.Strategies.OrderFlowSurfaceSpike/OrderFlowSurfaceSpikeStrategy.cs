using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OrderFlowSurfaceSpike;

public sealed class OrderFlowSurfaceSpikeStrategy : ITradingStrategy
{
    public string Id => "orderflow.surface.spike";
    public string DisplayName => "Order Flow Surface Spike";
    public string Description => "3D Z-score surface over a rolling [time slice × price bin] matrix of signed order flow. Enters in the direction of a confirmed spike in the latest slice; exits on TP/SL or Z reversion.";
}
