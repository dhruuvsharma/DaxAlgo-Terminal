namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Bookmap Reversal Type: seals when price reverses <see cref="_ticks"/> ticks from the
/// extreme of the forming bar in the active direction (pullback from high while up, or from
/// low while down). One-way continuation does not seal ??that is Range Type.
/// </summary>
public sealed class FootprintReversalBucketer : IFootprintBucketer
{
    private enum Dir : byte { None, Up, Down }

    private readonly long _ticks;
    private readonly double _tickSize;
    private readonly FeedQuality _quality;
    private readonly FootprintExtractorOptions _options;
    private readonly List<FootprintPrint> _prints = new();
    private DateTime _startUtc = DateTime.MinValue;
    private double _high = double.NegativeInfinity;
    private double _low = double.PositiveInfinity;
    private Dir _dir = Dir.None;
    private long _cumulativeDelta;

    public FootprintReversalBucketer(long ticks, double tickSize, FeedQuality quality,
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

        var reverseDistance = _ticks * _tickSize;

        // Establish direction from the first move off the open.
        if (_dir == Dir.None && print.Price != _high)
            _dir = print.Price > _high ? Dir.Up : Dir.Down;

        FootprintBar? sealedBar = null;
        var reverseDown = _dir == Dir.Up && print.Price <= _high - reverseDistance;
        var reverseUp = _dir == Dir.Down && print.Price >= _low + reverseDistance;

        if (_prints.Count > 0 && (reverseDown || reverseUp))
        {
            sealedBar = FootprintFeatures.BuildBar(_prints, _tickSize, _startUtc, print.TimeUtc,
                _quality, _cumulativeDelta, _options);
            _cumulativeDelta += sealedBar.Delta;
            _prints.Clear();
            _startUtc = print.TimeUtc;
            _high = _low = print.Price;
            _dir = Dir.None;
        }

        _prints.Add(print);
        if (print.Price > _high) _high = print.Price;
        if (print.Price < _low) _low = print.Price;
        if (_dir == Dir.None && _prints.Count >= 2)
            _dir = _high > _low
                ? (print.Price >= _high ? Dir.Up : Dir.Down)
                : Dir.None;
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
        _high = double.NegativeInfinity;
        _low = double.PositiveInfinity;
        _dir = Dir.None;
        _cumulativeDelta = cumulativeDeltaSeed;
    }
}
