using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.GapFade;

public sealed class GapFadeStrategy : ITradingStrategy
{
    public string Id => "gap.fade";
    public string DisplayName => "Overnight gap fade (index)";
    public string Description => "Detect overnight gap by inter-tick time delta + price jump; fade toward previous close.";
}