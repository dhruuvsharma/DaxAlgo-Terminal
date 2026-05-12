using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.IcebergDetection;

public sealed class IcebergDetectionStrategy : ITradingStrategy
{
    public string Id => "iceberg.detection";
    public string DisplayName => "Iceberg / hidden-liquidity detector (L2)";
    public string Description => "Sticky-touch heuristic - price stays unchanged on one side across N ticks => iceberg support.";
}