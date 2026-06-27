namespace TradingTerminal.Core.Backtest.Fast;

/// <summary>
/// Out-of-process replay engine. Runs in a separate subprocess (the C++20
/// <c>tick_backtester.exe</c> built from <c>tools/cpp-backtester/</c>), reachable only when
/// that binary has been compiled and dropped next to the App assembly. Strategies that
/// flag <c>BacktestStrategyOption.Fast = false</c> are not supported by the C++ engine and
/// must be routed through the managed <see cref="IBacktestSession"/> instead.
///
/// Throws <see cref="FastBacktestUnavailableException"/> when the binary is missing — the
/// caller is expected to surface that gracefully ("Fast backtest unavailable — falling
/// back to managed engine") rather than treating it as a hard failure.
/// </summary>
public interface IFastBacktestRunner
{
    bool IsAvailable { get; }

    Task<FastBacktestResult> RunAsync(FastBacktestRequest request, CancellationToken ct = default);
}

public sealed class FastBacktestUnavailableException : Exception
{
    public FastBacktestUnavailableException(string message) : base(message) { }
    public FastBacktestUnavailableException(string message, Exception inner) : base(message, inner) { }
}
