using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.PullbackContinuation;

public sealed class PullbackContinuationStrategy : ITradingStrategy
{
    public string Id => "pullback.continuation";
    public string DisplayName => "Trend pullback continuation (index)";
    public string Description => "200-period trend filter + N-tick pullback + resumption entry. Buy-the-dip with a filter.";
}