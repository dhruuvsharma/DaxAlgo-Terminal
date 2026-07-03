using System.Diagnostics;
using System.Threading.Channels;

namespace TradingTerminal.Infrastructure.Threading;

/// <summary>
/// Factory for the bounded, drop-oldest channels every live-feed bridge must use. An unbounded
/// channel between a fast producer (broker callback, hub fanout) and a consumer that can stall
/// (UI drain, slow subscriber) has no memory ceiling — the Volume Footprint window once piled up
/// ~20 GB of backlog exactly this way — so live bridges cap the queue and shed the OLDEST item
/// under pressure: the newest event always wins, and accuracy is aggregate, not per-event.
/// Canonical persistence is unaffected — the store writer has its own bounded queue — these
/// bridges only fan events out to consumers.
/// </summary>
public static class FeedChannel
{
    /// <summary>Per-stream queue ceilings (items). Sized so a healthy consumer never sees a
    /// drop — several seconds of the fastest observed cadence for each stream kind — and a
    /// stalled one sheds stale events instead of piling gigabytes.</summary>
    public static class Capacity
    {
        /// <summary>L1 quote/tick bridges (bid/ask updates).</summary>
        public const int Quotes = 16_384;

        /// <summary>Bar bridges — generous enough to hold a historical warm-up prefix
        /// (e.g. IB's keepUpToDate backfill) plus the live tail without shedding.</summary>
        public const int Bars = 8_192;

        /// <summary>Trade-tape bridges (every print; the fastest stream).</summary>
        public const int Trades = 65_536;

        /// <summary>Depth-snapshot bridges — snapshots supersede each other, so only a short
        /// queue of the newest is ever useful.</summary>
        public const int Depth = 2_048;
    }

    /// <summary>Creates a bounded channel that sheds the oldest item when full. Pass
    /// <paramref name="onItemDropped"/> (typically gated by a <see cref="FeedDropMeter"/>) to
    /// surface shedding in the log instead of losing events silently.</summary>
    public static Channel<T> CreateDropOldest<T>(
        int capacity,
        bool singleReader = true,
        bool singleWriter = false,
        Action<T>? onItemDropped = null)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = singleReader,
            SingleWriter = singleWriter,
        };
        return onItemDropped is null
            ? Channel.CreateBounded<T>(options)
            : Channel.CreateBounded<T>(options, onItemDropped);
    }
}

/// <summary>
/// Counts shed items for one bridge and rate-limits how often the caller logs about it:
/// <see cref="Record"/> returns <c>true</c> on the first drop and then at most once per 30 s,
/// so a sustained backlog produces a heartbeat warning carrying the running total instead of a
/// log flood. Thread-safe; drop callbacks run on the writer's thread.
/// </summary>
public sealed class FeedDropMeter
{
    private static readonly long LogIntervalStamp = Stopwatch.Frequency * 30L;

    private long _dropped;
    private long _lastLogStamp;

    /// <summary>Total items shed since the bridge opened.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Records one shed item; returns <c>true</c> when the caller should emit its
    /// rate-limited warning (first drop, then every 30 s while shedding continues).</summary>
    public bool Record()
    {
        Interlocked.Increment(ref _dropped);
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastLogStamp);
        if (last != 0 && now - last < LogIntervalStamp) return false;
        return Interlocked.CompareExchange(ref _lastLogStamp, now, last) == last;
    }
}
