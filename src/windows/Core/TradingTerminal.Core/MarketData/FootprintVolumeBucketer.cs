namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Bookmap Volume Type: seals when total traded size in the forming bar reaches
/// <see cref="_volumeThreshold"/>.
/// </summary>
public sealed class FootprintVolumeBucketer : IFootprintBucketer
{
    private readonly long _volumeThreshold;
    private readonly double _tickSize;
    private readonly FeedQuality _quality;
    private readonly FootprintExtractorOptions _options;
    private readonly List<FootprintPrint> _prints = new();
    private DateTime _startUtc = DateTime.MinValue;
    private long _volume;
    private long _cumulativeDelta;

    public FootprintVolumeBucketer(long volumeThreshold, double tickSize, FeedQuality quality,
        FootprintExtractorOptions options = default)
    {
        if (volumeThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(volumeThreshold));
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize));
        _volumeThreshold = volumeThreshold;
        _tickSize = tickSize;
        _quality = quality;
        _options = options.Equals(default(FootprintExtractorOptions))
            ? FootprintExtractorOptions.Default
            : options;
    }

    public long CumulativeDelta => _cumulativeDelta;

    public FootprintBar? Add(FootprintPrint print)
    {
        if (_startUtc == DateTime.MinValue)
            _startUtc = print.TimeUtc;

        _prints.Add(print);
        if (print.Size > 0) _volume += print.Size;

        if (_volume < _volumeThreshold)
            return null;

        var sealedBar = FootprintFeatures.BuildBar(_prints, _tickSize, _startUtc, print.TimeUtc,
            _quality, _cumulativeDelta, _options);
        _cumulativeDelta += sealedBar.Delta;
        _prints.Clear();
        _startUtc = DateTime.MinValue;
        _volume = 0;
        return sealedBar;
    }

    public FootprintBar? BuildForming() =>
        _startUtc == DateTime.MinValue || _prints.Count == 0
            ? null
            : FootprintFeatures.BuildBar(_prints, _tickSize, _startUtc,
                _prints[^1].TimeUtc, _quality, _cumulativeDelta, _options);

    public void Reset(long cumulativeDeltaSeed = 0)
    {
        _prints.Clear();
        _startUtc = DateTime.MinValue;
        _volume = 0;
        _cumulativeDelta = cumulativeDeltaSeed;
    }
}
