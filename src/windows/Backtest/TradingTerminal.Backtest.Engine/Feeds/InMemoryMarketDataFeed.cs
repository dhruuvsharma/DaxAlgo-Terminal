using System.Runtime.CompilerServices;
using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.Backtest.Engine.Feeds;

/// <summary>
/// A feed backed by an in-memory event list — the workhorse for tests, the synthetic generator, and
/// any caller that has already materialized events. Events are sorted by timestamp on construction so
/// callers don't have to pre-sort. Store- and parquet-backed feeds (which stream lazily) land in P2.
/// </summary>
public sealed class InMemoryMarketDataFeed : IMarketDataFeed
{
    private readonly IReadOnlyList<MarketEvent> _events;

    public InMemoryMarketDataFeed(IEnumerable<MarketEvent> events) =>
        _events = events.OrderBy(e => e.TimestampUtc).ToList();

    public async IAsyncEnumerable<MarketEvent> StreamAsync(
        RunSpec spec, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask; // keep the async-iterator contract without per-event yielding
        foreach (var e in _events)
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
    }
}
