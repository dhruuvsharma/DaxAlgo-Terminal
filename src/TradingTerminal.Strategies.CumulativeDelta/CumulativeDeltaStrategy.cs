using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.CumulativeDelta;

public sealed class CumulativeDeltaStrategy : ITradingStrategy
{
    public string Id => "cumulative.delta.scalper";
    public string DisplayName => "Cumulative Delta Scalper";
    public string Description =>
        "Sniper-mode tick delta scalper. Bid-tick uptick/downtick → bar deltas → window crossover of " +
        "±threshold, gated by 5 confirmations (momentum, HTF EMA, EMA slope, ADX, dynamic spread). " +
        "Multi-session GMT (Asia/London/NY/Overlap), per-session and daily caps, inter-signal cooldown. " +
        "Display only — does not place orders.";
}
