using System.Runtime.CompilerServices;

namespace TradingTerminal.Backtest.Engine.Feeds;

/// <summary>
/// K-way merge of already-ascending <see cref="MarketEvent"/> streams into one globally
/// time-ordered stream, using a min-heap over each source's head. This is how a portfolio run
/// interleaves many instruments (and each instrument's quote/trade streams) into the single timeline
/// the engine replays. On equal timestamps a quote sorts before a trade so the strategy's view of the
/// spread is current when it sees the print — matching the legacy engine's tie-break.
/// </summary>
internal static class AsyncMerge
{
    public static async IAsyncEnumerable<MarketEvent> ByEventTime(
        IReadOnlyList<IAsyncEnumerable<MarketEvent>> sources,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var enumerators = new List<IAsyncEnumerator<MarketEvent>>(sources.Count);
        // Priority = (timestamp, kind-rank, source-index): deterministic ordering even on ties.
        var heap = new PriorityQueue<int, (DateTime Ts, int Rank, int Idx)>();
        try
        {
            for (var i = 0; i < sources.Count; i++)
            {
                var e = sources[i].GetAsyncEnumerator(ct);
                enumerators.Add(e);
                if (await e.MoveNextAsync().ConfigureAwait(false))
                    heap.Enqueue(i, KeyOf(e.Current, i));
            }

            while (heap.Count > 0)
            {
                var i = heap.Dequeue();
                var e = enumerators[i];
                yield return e.Current;
                if (await e.MoveNextAsync().ConfigureAwait(false))
                    heap.Enqueue(i, KeyOf(e.Current, i));
            }
        }
        finally
        {
            foreach (var e in enumerators)
                await e.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static (DateTime, int, int) KeyOf(MarketEvent ev, int idx) =>
        (ev.TimestampUtc, ev.Kind == MarketEventKind.Quote ? 0 : 1, idx);
}
