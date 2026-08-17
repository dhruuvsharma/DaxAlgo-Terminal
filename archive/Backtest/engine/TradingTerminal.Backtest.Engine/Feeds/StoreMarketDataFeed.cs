using System.Runtime.CompilerServices;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Backtest.Engine.Feeds;

/// <summary>
/// Replays a run from the canonical market-data store — the primary data path for the new engine.
/// For every instrument in the <see cref="Universe"/> it opens the stored quote and trade streams
/// (each already ascending by event time, scoped to the instrument's broker source), then merges all
/// of them into one global timeline via <see cref="AsyncMerge"/>. A single-instrument universe is the
/// classic backtest; a multi-instrument universe is a portfolio run, interleaved here.
/// </summary>
public sealed class StoreMarketDataFeed : IMarketDataFeed
{
    private readonly IMarketDataStore _store;

    public StoreMarketDataFeed(IMarketDataStore store) => _store = store;

    public IAsyncEnumerable<MarketEvent> StreamAsync(RunSpec spec, CancellationToken ct)
    {
        if (spec.Data.FromUtc is not { } from || spec.Data.ToUtc is not { } to)
            throw new InvalidOperationException("StoreMarketDataFeed requires DataSpec.FromUtc and DataSpec.ToUtc.");
        if (to <= from)
            throw new InvalidOperationException("StoreMarketDataFeed requires ToUtc > FromUtc.");

        var sources = new List<IAsyncEnumerable<MarketEvent>>(spec.Universe.Instruments.Count * 2);
        foreach (var inst in spec.Universe.Instruments)
        {
            sources.Add(Quotes(inst, from, to, ct));
            sources.Add(Trades(inst, from, to, ct));
        }
        return AsyncMerge.ByEventTime(sources, ct);
    }

    private async IAsyncEnumerable<MarketEvent> Quotes(
        InstrumentSpec inst, DateTime from, DateTime to, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var q in _store.ReadQuotesAsync(inst.Id, from, to, inst.Source, ct).WithCancellation(ct))
            yield return MarketEvent.OfQuote(inst.Id, new Tick(q.EventTimeUtc, q.Bid, q.Ask, q.BidSize, q.AskSize));
    }

    private async IAsyncEnumerable<MarketEvent> Trades(
        InstrumentSpec inst, DateTime from, DateTime to, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var t in _store.ReadTradesAsync(inst.Id, from, to, inst.Source, ct).WithCancellation(ct))
            yield return MarketEvent.OfTrade(inst.Id, t);
    }
}
