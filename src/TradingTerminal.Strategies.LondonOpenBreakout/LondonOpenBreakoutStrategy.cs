using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.LondonOpenBreakout;

public sealed class LondonOpenBreakoutStrategy : ITradingStrategy
{
    public string Id => "london.open.breakout";
    public string DisplayName => "London-open breakout (forex)";
    public string Description => "Asian-session range + 08:00 UTC breakout, ATR trailing stop, flat at 16:00 UTC.";
}