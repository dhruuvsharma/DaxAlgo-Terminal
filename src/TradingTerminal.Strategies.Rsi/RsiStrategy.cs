using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.Rsi;

public sealed class RsiStrategy : ITradingStrategy
{
    public string Id => "rsi.overbought.oversold";
    public string DisplayName => "RSI";
    public string Description => "Relative Strength Index overbought/oversold signal. Pick an instrument, set the thresholds, then arm the algo.";
}
