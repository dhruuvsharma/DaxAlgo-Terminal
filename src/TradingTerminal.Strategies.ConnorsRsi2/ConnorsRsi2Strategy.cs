using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.ConnorsRsi2;

public sealed class ConnorsRsi2Strategy : ITradingStrategy
{
    public string Id => "connors.rsi2";
    public string DisplayName => "Connors RSI(2) reversion (forex)";
    public string Description => "Larry Connors RSI(2). Buy at RSI â‰¤ entry; exit at RSI â‰¥ exit OR close above 5-SMA.";
}