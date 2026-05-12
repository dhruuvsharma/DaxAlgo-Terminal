using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.MaCrossover;

public sealed class MaCrossoverStrategy : ITradingStrategy
{
    public string Id => "ma.crossover";
    public string DisplayName => "MA crossover / golden cross (forex)";
    public string Description => "Fast / slow SMA crossover. Always-in-market: flips between long and short on every cross.";
}