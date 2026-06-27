namespace TradingTerminal.Core.Time;

/// <summary>
/// Wall-clock abstraction. Real code uses <c>SystemClock</c> (<see cref="DateTime.UtcNow"/>);
/// the backtest engine substitutes a <c>SimulatedClock</c> that advances as historical
/// ticks are replayed. Any code that conditions on time and needs to be deterministic
/// under backtests should take <see cref="IClock"/> rather than calling <c>DateTime.UtcNow</c>
/// directly.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}
