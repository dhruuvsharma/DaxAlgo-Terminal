using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.VolatilityTargeted;

public sealed class VolatilityTargetedStrategy : ITradingStrategy
{
    public string Id => "vol.targeted";
    public string DisplayName => "Volatility targeting (index)";
    public string Description => "Position = target_vol / realised_vol_ewma. AQR-style risk-parity overlay.";
}