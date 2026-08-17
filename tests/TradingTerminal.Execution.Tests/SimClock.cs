using TradingTerminal.Core.Time;

namespace TradingTerminal.Execution.Tests;

/// <summary>
/// Settable clock for the execution tests: every one of them drives time explicitly so fills,
/// timeouts and lease fencing are deterministic rather than wall-clock dependent.
///
/// <para>It used to live in <c>TradingTerminal.Backtest.Engine</c> and came along with that project's
/// reference. The backtest engine was archived on 2026-08-17; this is test-only code and no
/// production type ever used it, so it moved here rather than keeping a dead project alive.</para>
/// </summary>
public sealed class SimClock : IClock
{
    public DateTime UtcNow { get; private set; } = DateTime.UnixEpoch;

    public void SetTo(DateTime utc) => UtcNow = utc;
}
