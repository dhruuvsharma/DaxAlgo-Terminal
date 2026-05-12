using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.Bollinger;

public sealed class BollingerStrategy : ITradingStrategy
{
    public string Id => "bollinger.reversion";
    public string DisplayName => "Bollinger band reversion (forex)";
    public string Description => "Long below the lower band, short above the upper; exit at SMA, stop at extreme-band breach.";
}