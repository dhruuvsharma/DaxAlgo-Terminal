using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.CumulativeDelta;

public sealed class CumulativeDeltaStrategy : ITradingStrategy
{
    public string Id => "cumulative.delta.scalper";
    public string DisplayName => "Cumulative Delta Scalper";
    public string Description =>
        "Tick-level cumulative delta scalper. Bid-tick rule classifies each tick as up/down; " +
        "summed bar deltas in a sliding window cross ±threshold to trigger. ATR + HTF EMA + " +
        "session/spread/cooldown guards. Display only — does not place orders.";
}
