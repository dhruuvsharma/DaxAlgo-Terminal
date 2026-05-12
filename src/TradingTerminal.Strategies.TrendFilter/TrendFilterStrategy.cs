using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.TrendFilter;

public sealed class TrendFilterStrategy : ITradingStrategy
{
    public string Id => "trend.filter";
    public string DisplayName => "200-SMA trend filter (index)";
    public string Description => "Long when price > long SMA, flat otherwise (Faber 2007 tactical-AA overlay).";
}