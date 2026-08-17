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
/// <para>
/// When <see cref="ModelingMode.EveryTickFromBars"/> is set, quotes/trades are skipped and each
/// stored 1m bar is expanded into an O→H→L→C (or O→L→H→C) synthetic L1 path — the green rung that
/// unlocks bar-only strategies without claiming real-tick fidelity.
/// </para>
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

        if (spec.Data.Modeling == ModelingMode.EveryTickFromBars)
            return StreamFromBarsAsync(spec, from, to, ct);

        var sources = new List<IAsyncEnumerable<MarketEvent>>(spec.Universe.Instruments.Count * 2);
        foreach (var inst in spec.Universe.Instruments)
        {
            sources.Add(Quotes(inst, from, to, ct));
            sources.Add(Trades(inst, from, to, ct));
        }
        return AsyncMerge.ByEventTime(sources, ct);
    }

    private IAsyncEnumerable<MarketEvent> StreamFromBarsAsync(
        RunSpec spec, DateTime from, DateTime to, CancellationToken ct)
    {
        var sources = new List<IAsyncEnumerable<MarketEvent>>(spec.Universe.Instruments.Count);
        foreach (var inst in spec.Universe.Instruments)
            sources.Add(BarsAsTicks(inst, from, to, ct));
        return AsyncMerge.ByEventTime(sources, ct);
    }

    private async IAsyncEnumerable<MarketEvent> BarsAsTicks(
        InstrumentSpec inst, DateTime from, DateTime to, [EnumeratorCancellation] CancellationToken ct)
    {
        var half = Math.Max(inst.TickSize, 1e-9) / 2.0;
        await foreach (var bar in _store.ReadBarsAsync(inst.Id, BarSize.OneMinute, from, to, inst.Source, ct)
                           .WithCancellation(ct))
        {
            var span = bar.Size.ToTimeSpan();
            var step = span / 4;
            var path = bar.Close >= bar.Open
                ? new[] { bar.Open, bar.Low, bar.High, bar.Close }
                : new[] { bar.Open, bar.High, bar.Low, bar.Close };
            var sizePer = Math.Max(1, bar.Volume / 4);
            for (var i = 0; i < path.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var px = path[i];
                var ts = bar.OpenTimeUtc + step * i;
                yield return MarketEvent.OfQuote(
                    inst.Id,
                    new Tick(ts, px - half, px + half, sizePer, sizePer));
            }
        }
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
