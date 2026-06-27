using TradingTerminal.Core.Time;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Backtest clock — <see cref="UtcNow"/> reads the value set by the engine's replay loop,
/// not wall time. Strategies that take <see cref="IClock"/> rather than calling
/// <c>DateTime.UtcNow</c> become deterministic under backtests.
/// </summary>
public sealed class SimulatedClock : IClock
{
    private DateTime _now = DateTime.UnixEpoch;

    public DateTime UtcNow => _now;

    /// <summary>Engine-only — advance the clock to the timestamp of the next replayed tick.</summary>
    public void SetTo(DateTime utc)
    {
        if (utc.Kind != DateTimeKind.Utc)
            utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        _now = utc;
    }
}
