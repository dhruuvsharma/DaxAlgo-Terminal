using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.EodMomentum;

public sealed class EodMomentumStrategy : ITradingStrategy
{
    public string Id => "eod.momentum";
    public string DisplayName => "End-of-day momentum (index)";
    public string Description => "Take direction of day's open-to-now return in the last fraction of the UTC session.";
}