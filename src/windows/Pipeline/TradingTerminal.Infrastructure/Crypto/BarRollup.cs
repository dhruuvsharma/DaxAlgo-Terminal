using TradingTerminal.Core.Domain;

namespace TradingTerminal.Infrastructure.Crypto;

/// <summary>
/// Builds bars of a size a venue does not publish, out of ones it does.
///
/// <para><b>Why this exists.</b> Most crypto venues publish no three-minute candle. The older adapters
/// answer a 3m request with the nearest size they have — Coinbase and Kraken send 5m bars, OANDA 2m —
/// labelled as 3m. A chart of those is wrong in a way nothing on screen reveals. The venues added on
/// 2026-09-25 roll one-minute bars up instead, which is exact: a 3m bar is its three minutes' first
/// open, highest high, lowest low, last close and summed volume.</para>
///
/// <para>Buckets align to the Unix epoch in UTC, as every venue's own candles do, so a rolled-up bar
/// starts where the venue's would have.</para>
/// </summary>
internal static class BarRollup
{
    /// <summary>The start of the bucket <paramref name="timeUtc"/> falls in.</summary>
    public static DateTime BucketStart(DateTime timeUtc, TimeSpan step)
    {
        var ticks = timeUtc.Ticks - (timeUtc.Ticks - DateTime.UnixEpoch.Ticks) % step.Ticks;
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary>
    /// Rolls <paramref name="bars"/> — any order, any smaller step — into <paramref name="step"/> buckets,
    /// oldest first. A bucket is emitted even when only part of it is present: the newest bucket is
    /// usually still forming, and dropping it would make the chart lag the venue by up to one bar.
    /// </summary>
    public static IReadOnlyList<Bar> Rollup(IEnumerable<Bar> bars, TimeSpan step)
    {
        var result = new List<Bar>();
        Bar? current = null;

        foreach (var bar in bars.OrderBy(b => b.TimestampUtc))
        {
            var start = BucketStart(bar.TimestampUtc, step);
            if (current is not null && current.TimestampUtc == start)
            {
                current = Merge(current, bar);
            }
            else
            {
                if (current is not null) result.Add(current);
                current = bar with { TimestampUtc = start };
            }
        }

        if (current is not null) result.Add(current);
        return result;
    }

    /// <summary>Sorts oldest first, drops duplicate timestamps (last one wins — a venue's in-progress
    /// bar can appear twice across a page boundary), and keeps the newest <paramref name="count"/>.</summary>
    public static IReadOnlyList<Bar> Normalise(IEnumerable<Bar> bars, int count)
    {
        var unique = new SortedDictionary<DateTime, Bar>();
        foreach (var bar in bars) unique[bar.TimestampUtc] = bar;
        var ordered = unique.Values.ToList();
        return ordered.Count <= count ? ordered : ordered.GetRange(ordered.Count - count, count);
    }

    private static Bar Merge(Bar into, Bar next) => into with
    {
        High = Math.Max(into.High, next.High),
        Low = Math.Min(into.Low, next.Low),
        Close = next.Close,
        Volume = into.Volume + next.Volume,
    };
}

/// <summary>
/// The live half of <see cref="BarRollup"/>: turns a stream of one-minute candle updates into the
/// in-progress bar of a larger size.
///
/// <para>A venue re-sends the forming minute many times as trades land, so each minute is held by its
/// start time and replaced, not added — summing the updates would count the same volume over and over.
/// When a new bucket begins the old minutes are dropped; nothing older than the current bucket is ever
/// needed.</para>
/// </summary>
internal sealed class LiveBarRollup(TimeSpan step)
{
    private readonly SortedDictionary<DateTime, Bar> _minutes = new();
    private DateTime _bucket;

    /// <summary>Takes one minute update and returns the bucket's bar as it now stands.</summary>
    public Bar Push(Bar minute)
    {
        var start = BarRollup.BucketStart(minute.TimestampUtc, step);
        if (start != _bucket)
        {
            // An update for an older bucket arriving late is folded into nothing — returning a stale
            // bucket would move the chart backwards.
            if (start < _bucket) return BarRollup.Rollup(_minutes.Values, step)[^1];
            _minutes.Clear();
            _bucket = start;
        }

        _minutes[minute.TimestampUtc] = minute;
        return BarRollup.Rollup(_minutes.Values, step)[^1];
    }
}

/// <summary>
/// Bars built from trades, for the venues that publish no candle stream at all (Bithumb, Bitstamp).
///
/// <para>Exact for every trade seen. The one thing it cannot know is what happened before the stream
/// started, so the first bar opens at the first trade received rather than the bucket's true open;
/// history for that period comes from the venue's REST candles.</para>
/// </summary>
internal sealed class LiveTradeBars(TimeSpan step)
{
    private Bar? _current;

    /// <summary>Takes one trade and returns the bucket's bar as it now stands.</summary>
    public Bar Push(TradeTick trade)
    {
        var start = BarRollup.BucketStart(trade.TimestampUtc, step);

        if (_current is null || start > _current.TimestampUtc)
        {
            _current = new Bar(start, trade.Price, trade.Price, trade.Price, trade.Price, trade.Size);
            return _current;
        }

        // A late print for a bucket already closed is dropped for the same reason as above.
        if (start < _current.TimestampUtc) return _current;

        _current = _current with
        {
            High = Math.Max(_current.High, trade.Price),
            Low = Math.Min(_current.Low, trade.Price),
            Close = trade.Price,
            Volume = _current.Volume + trade.Size,
        };
        return _current;
    }
}
