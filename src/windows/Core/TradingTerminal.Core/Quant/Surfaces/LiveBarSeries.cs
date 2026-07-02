using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Quant.Surfaces;

/// <summary>
/// Rolling bar window for the live Surface Lab: seeded from history, then fed live prices
/// (quote mids) and trade sizes. Prices update the forming bar's OHLC and roll it over when the
/// wall-clock bucket advances; trade sizes accumulate into the forming bar's volume. Retention
/// is hard-capped, so an always-on window can never grow its memory. Not thread-safe — the
/// owning view-model calls it from the UI thread only (batch-drained pumps marshal there).
/// </summary>
public sealed class LiveBarSeries
{
    private readonly TimeSpan _interval;
    private readonly int _maxBars;
    private readonly List<Bar> _bars = new();

    private DateTime _formingOpenUtc;
    private double _open, _high, _low, _close;
    private long _volume;
    private bool _hasForming;

    public LiveBarSeries(TimeSpan interval, int maxBars)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _interval = interval;
        _maxBars = Math.Max(10, maxBars);
    }

    /// <summary>Committed bars + the forming bar (if any).</summary>
    public int Count => _bars.Count + (_hasForming ? 1 : 0);

    public DateTime? LastUpdateUtc { get; private set; }

    /// <summary>Replaces the window with historical bars (oldest first), trimmed to the cap.
    /// Live prices timestamped inside or before the last seeded bar are clamped forward into
    /// the next bucket, so the seed and the live tail never overlap.</summary>
    public void Seed(IReadOnlyList<Bar> history)
    {
        _bars.Clear();
        _hasForming = false;
        var start = Math.Max(0, history.Count - _maxBars);
        for (var i = start; i < history.Count; i++) _bars.Add(history[i]);
        LastUpdateUtc = _bars.Count > 0 ? _bars[^1].TimestampUtc : null;
    }

    /// <summary>Feeds one live price (quote mid or trade price). Opens/rolls the forming bar.</summary>
    public void PushPrice(DateTime eventTimeUtc, double price)
    {
        if (price <= 0 || double.IsNaN(price)) return;

        var bucket = BucketOpen(eventTimeUtc);
        // Never rewind: a late tick lands in the current forming bar; ticks inside the seeded
        // history's last bar start the bucket right after it.
        if (_bars.Count > 0 && bucket <= _bars[^1].TimestampUtc)
            bucket = _bars[^1].TimestampUtc + _interval;
        if (_hasForming && bucket < _formingOpenUtc)
            bucket = _formingOpenUtc;

        if (!_hasForming)
        {
            StartForming(bucket, price);
        }
        else if (bucket > _formingOpenUtc)
        {
            CommitForming();
            StartForming(bucket, price);
        }
        else
        {
            if (price > _high) _high = price;
            if (price < _low) _low = price;
            _close = price;
        }
        LastUpdateUtc = eventTimeUtc;
    }

    /// <summary>Accumulates a trade's size into the forming bar (ignored before the first price).</summary>
    public void PushVolume(long size)
    {
        if (_hasForming && size > 0) _volume += size;
    }

    /// <summary>A point-in-time copy: committed bars plus the forming bar. Safe to hand to a
    /// background grid rebuild while the UI thread keeps pushing ticks.</summary>
    public Bar[] Snapshot()
    {
        var result = new Bar[Count];
        _bars.CopyTo(result);
        if (_hasForming)
            result[^1] = new Bar(_formingOpenUtc, _open, _high, _low, _close, _volume);
        return result;
    }

    private DateTime BucketOpen(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % _interval.Ticks, DateTimeKind.Utc);

    private void StartForming(DateTime openUtc, double price)
    {
        _formingOpenUtc = openUtc;
        _open = _high = _low = _close = price;
        _volume = 0;
        _hasForming = true;
    }

    private void CommitForming()
    {
        _bars.Add(new Bar(_formingOpenUtc, _open, _high, _low, _close, _volume));
        while (_bars.Count > _maxBars) _bars.RemoveAt(0);
        _hasForming = false;
    }
}
