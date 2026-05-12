using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.Twap;

public sealed class TwapStrategy : ITradingStrategy
{
    public string Id => "twap.execution";
    public string DisplayName => "TWAP buy execution";
    public string Description => "Splits a parent order into N equal market children, fired evenly. Mirrors broker TWAP algos.";
}