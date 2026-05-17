using TradingTerminal.Core.Backtest.Fast;

namespace TradingTerminal.Infrastructure.Backtest.Fast;

/// <summary>
/// Fallback implementation registered when the C++ tick backtester binary is not present
/// next to the App assembly. Reports <see cref="IsAvailable"/> = false and throws on every
/// call to <see cref="RunAsync"/>. Callers should check <see cref="IsAvailable"/> before
/// presenting a "Fast" toggle in the UI.
/// </summary>
public sealed class NullFastBacktestRunner : IFastBacktestRunner
{
    private readonly string _reason;

    public NullFastBacktestRunner(string reason)
    {
        _reason = reason;
    }

    public bool IsAvailable => false;

    public Task<FastBacktestResult> RunAsync(FastBacktestRequest request, CancellationToken ct = default) =>
        throw new FastBacktestUnavailableException(_reason);
}
