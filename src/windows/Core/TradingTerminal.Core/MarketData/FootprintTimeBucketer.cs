namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Stateful time-bucketer for footprint bars: accumulates <see cref="FootprintPrint"/>s into
/// fixed wall-clock buckets (<c>bucket = ts − ts mod span</c>) and seals the prior bucket into a
/// <see cref="FootprintBar"/> via <see cref="FootprintFeatures.BuildBar"/> when a print rolls
/// into a different bucket. Threads the running cumulative delta across sealed bars.
///
/// This is the single bucketing implementation shared by the Volume Footprint window's live
/// stream and its ML warm-start backfill, so historical training bars are built through the
/// exact same path as live ones. Single-threaded; the caller confines it.
///
/// Sealing conventions (mirrors the original inline window logic): a seal fires on <em>any</em>
/// bucket change with prints present — including a print whose timestamp maps to an older
/// bucket — and the sealed bar's <c>EndUtc</c> is the new bucket's start. The forming
/// (unsealed) bar from <see cref="BuildForming"/> instead ends at <c>start + span</c>.
/// </summary>
public sealed class FootprintTimeBucketer
{
    private readonly TimeSpan _span;
    private readonly double _tickSize;
    private readonly FeedQuality _quality;
    private readonly List<FootprintPrint> _prints = new();
    private DateTime _bucketStart = DateTime.MinValue;
    private long _cumulativeDelta;

    public FootprintTimeBucketer(TimeSpan span, double tickSize, FeedQuality quality)
    {
        if (span <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(span));
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize));
        _span = span;
        _tickSize = tickSize;
        _quality = quality;
    }

    /// <summary>Start of the bucket currently accumulating, or <see cref="DateTime.MinValue"/>
    /// when no print has arrived yet.</summary>
    public DateTime CurrentBucketStart => _bucketStart;

    /// <summary>Cumulative delta through the last <em>sealed</em> bar (the forming bar's delta
    /// is not folded in until it seals).</summary>
    public long CumulativeDelta => _cumulativeDelta;

    /// <summary>Adds one print. Returns the sealed prior-bucket bar when this print opened a new
    /// bucket and the prior bucket held prints; null otherwise (including the very first print).</summary>
    public FootprintBar? Add(FootprintPrint print)
    {
        var ts = print.TimeUtc;
        var bucket = new DateTime(ts.Ticks - ts.Ticks % _span.Ticks, DateTimeKind.Utc);

        FootprintBar? sealedBar = null;
        if (bucket != _bucketStart)
        {
            if (_bucketStart != DateTime.MinValue && _prints.Count > 0)
            {
                sealedBar = FootprintFeatures.BuildBar(_prints, _tickSize, _bucketStart, bucket,
                    _quality, _cumulativeDelta);
                _cumulativeDelta += sealedBar.Delta;
            }
            _bucketStart = bucket;
            _prints.Clear();
        }

        _prints.Add(print);
        return sealedBar;
    }

    /// <summary>Rebuilds the forming (unsealed) bar from the accumulated prints — the render-tick
    /// path. Null when no bucket is open.</summary>
    public FootprintBar? BuildForming() =>
        _bucketStart == DateTime.MinValue
            ? null
            : FootprintFeatures.BuildBar(_prints, _tickSize, _bucketStart, _bucketStart + _span,
                _quality, _cumulativeDelta);

    public void Reset(long cumulativeDeltaSeed = 0)
    {
        _prints.Clear();
        _bucketStart = DateTime.MinValue;
        _cumulativeDelta = cumulativeDeltaSeed;
    }
}
