using TradingTerminal.Core.Risk;

namespace TradingTerminal.Core.Backtest;

/// <summary>
/// View-model-facing seam over the backtest engine. The Backtest tab's view-model injects
/// this rather than newing up the concrete <c>BacktestSession</c> directly, so tests can
/// substitute a fake that returns canned results without parquet I/O.
/// </summary>
public interface IBacktestSession
{
    /// <summary>
    /// Runs a single backtest end-to-end. <paramref name="risk"/> is optional; pass
    /// <c>null</c> to disable risk checks.
    /// </summary>
    Task<BacktestResult> RunAsync(
        BacktestConfig config,
        IBacktestStrategy strategy,
        IRiskManager? risk = null,
        CancellationToken ct = default);
}
