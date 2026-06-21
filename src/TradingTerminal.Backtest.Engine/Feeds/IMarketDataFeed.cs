using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.Backtest.Engine.Feeds;

/// <summary>
/// Produces the time-ordered event stream the engine replays for a run. Implementations decide
/// where data comes from (the canonical store, a parquet file, a synthetic generator) and, for
/// portfolio runs, are responsible for k-way merging every instrument's events into one
/// chronological stream. Cancellation is the stop signal.
/// </summary>
public interface IMarketDataFeed
{
    IAsyncEnumerable<MarketEvent> StreamAsync(RunSpec spec, CancellationToken ct);
}
