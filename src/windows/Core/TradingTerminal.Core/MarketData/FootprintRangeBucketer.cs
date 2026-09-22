namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Bookmap Range Type: seals when the forming bar's high?’low exceeds <see cref="_ticks"/> ticks.
/// </summary>
public sealed class FootprintRangeBucketer : IFootprintBucketer
{
    private readonly long _ticks;
    private readonly double _tickSize;
    private readonly FeedQuality _quality;
    private readonly FootprintExtractorOptions _options;
    private readonly List<FootprintPrint> _prints = new();
    private DateTime _startUtc = DateTime.MinValue;
    private double _high = double.NegativeInfinity;
    private double _low = double.PositiveInfinity;
    private long _cumulativeDelta;

    public FootprintRangeBucketer(long ticks, double tickSize, FeedQuality quality,
        FootprintExtractorOptions options = default)
    {
        if (ticks <= 0) throw new ArgumentOutOfRangeException(nameof(ticks));
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize));
        _ticks = ticks;
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
        {
            _startUtc = print.TimeUtc;
            _high = _low = print.Price;
            _prints.Add(print);
            return null;
        }

        var nextHigh = Math.Max(_high, print.Price);
        var nextLow = Math.Min(_low, print.Price);
        var rangeTicks = (nextHigh - nextLow) / _tickSize;

        _prints.Add(print);
        _high = nextHigh;
        _low = nextLow;

        if (_prints.Count > 1 && rangeTicks >= _ticks)
        {
            var sealedBar = FootprintFeatures.BuildBar(_prints, _tickSize, _startUtc, print.TimeUtc,
                _quality, _cumulativeDelta, _options);
            _cumulativeDelta += sealedBar.Delta;
            _prints.Clear();
            _startUtc = DateTime.MinValue;
            _high = double.NegativeInfinity;
            _low = double.PositiveInfinity;
            return sealedBar;
        }

        return null;
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
        _high = double.NegativeInfinity;
        _low = double.PositiveInfinity;
        _cumulativeDelta = cumulativeDeltaSeed;
    }
}
