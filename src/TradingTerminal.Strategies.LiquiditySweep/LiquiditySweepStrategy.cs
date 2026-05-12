using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.LiquiditySweep;

public sealed class LiquiditySweepStrategy : ITradingStrategy
{
    public string Id => "liquidity.sweep";
    public string DisplayName => "Liquidity-sweep detector (L2)";
    public string Description => "Detect rapid depletion of touch size combined with same-side price drop. Momentum follow.";
}