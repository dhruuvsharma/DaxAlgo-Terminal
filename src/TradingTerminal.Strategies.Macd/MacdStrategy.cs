using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.Macd;

public sealed class MacdStrategy : ITradingStrategy
{
    public string Id => "macd.crossover";
    public string DisplayName => "MACD signal crossover (forex)";
    public string Description => "12/26/9 MACD vs signal line; flip direction on every cross.";
}