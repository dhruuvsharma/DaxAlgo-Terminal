using TradingTerminal.Core.Time;

namespace TradingTerminal.Backtest.Engine;

/// <summary>
/// The backtest clock: <see cref="UtcNow"/> is whatever the engine last advanced it to as it
/// replayed events, so any kernel code that reads the clock sees deterministic, replay-driven time
/// instead of wall time.
/// </summary>
public sealed class SimClock : IClock
{
    public DateTime UtcNow { get; private set; } = DateTime.UnixEpoch;

    public void SetTo(DateTime utc) => UtcNow = utc;
}
