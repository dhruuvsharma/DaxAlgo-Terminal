using TradingTerminal.Backtest.Protocol;

namespace TradingTerminal.Infrastructure.Backtest.Worker;

/// <summary>Runs one isolated worker process and returns a fully verified terminal outcome.</summary>
public interface IBacktestJobClient
{
    Task<BacktestJobOutcome> RunAsync(
        BacktestJobRequest request,
        IProgress<BacktestJobProgress>? progress = null,
        CancellationToken ct = default);
}
