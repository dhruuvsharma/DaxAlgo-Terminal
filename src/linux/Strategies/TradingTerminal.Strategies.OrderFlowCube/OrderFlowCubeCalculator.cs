using TradingTerminal.Core.Domain;

namespace TradingTerminal.Strategies.OrderFlowCube;

/// <summary>
/// Live rolling calculator for the three Order Flow Cube signals. Stateful — one instance per
/// streamed instrument, fed trade-by-trade. Three independent windows are used on purpose so the
/// signals are <em>actually orthogonal</em> (computed over the same window, CVD ≈ 2·aggressor − 1
/// and all sample points collapse onto a line — the "cube" becomes degenerate):
/// <list type="bullet">
/// <item><b>Recent window</b> — aggressor-ratio sample. Short (default 50 trades) ⇒ "current pressure".</item>
/// <item><b>Trend window</b> — CVD-imbalance sample. Longer (default 500 trades) ⇒ "directional flow over the arc".</item>
/// <item><b>Baseline window</b> — average trade-size denominator. Long (default 2000 trades) ⇒
/// the "what's normal" reference; numerator is the recent-window average size.</item>
/// </list>
/// Trades with <see cref="AggressorSide.Unknown"/> contribute to the baseline (so the average size
/// estimate isn't biased by dropping them) but not to the directional signals.
/// </summary>
public sealed class OrderFlowCubeCalculator
{
    public int RecentWindow { get; }
    public int TrendWindow { get; }
    public int BaselineWindow { get; }

    private readonly Queue<(long Volume, bool IsBuy)> _recent;
    private readonly Queue<(long Volume, bool IsBuy)> _trend;
    private readonly Queue<long> _baseline;
    private long _recentBuy, _recentSell, _recentTotal;
    private long _trendBuy, _trendSell, _trendTotal;
    private long _baselineSizeSum;

    public OrderFlowCubeCalculator(
        int recentWindow = 50,
        int trendWindow = 500,
        int baselineWindow = 2000)
    {
        if (recentWindow < 1) throw new ArgumentOutOfRangeException(nameof(recentWindow));
        if (trendWindow < recentWindow) throw new ArgumentOutOfRangeException(nameof(trendWindow), "trendWindow must be >= recentWindow.");
        if (baselineWindow < trendWindow) throw new ArgumentOutOfRangeException(nameof(baselineWindow), "baselineWindow must be >= trendWindow.");
        RecentWindow = recentWindow;
        TrendWindow = trendWindow;
        BaselineWindow = baselineWindow;
        _recent = new Queue<(long, bool)>(recentWindow);
        _trend = new Queue<(long, bool)>(trendWindow);
        _baseline = new Queue<long>(baselineWindow);
    }

    /// <summary>True once each window has at least min-warmup samples to produce meaningful signals.</summary>
    public bool IsWarm =>
        _recent.Count >= RecentWindow
        && _trend.Count >= TrendWindow
        && _baseline.Count >= Math.Max(TrendWindow, BaselineWindow / 4)
        && _recentTotal > 0
        && _trendTotal > 0
        && _baselineSizeSum > 0;

    public void Reset()
    {
        _recent.Clear(); _trend.Clear(); _baseline.Clear();
        _recentBuy = _recentSell = _recentTotal = 0;
        _trendBuy = _trendSell = _trendTotal = 0;
        _baselineSizeSum = 0;
    }

    public void Add(TradePrint trade)
    {
        // Baseline takes all trades regardless of aggressor (avoiding selection bias on size).
        _baseline.Enqueue(trade.Size);
        _baselineSizeSum += trade.Size;
        while (_baseline.Count > BaselineWindow)
            _baselineSizeSum -= _baseline.Dequeue();

        var isBuy = trade.Aggressor == AggressorSide.Buy;
        var isSell = trade.Aggressor == AggressorSide.Sell;
        if (!isBuy && !isSell) return;

        // Recent window.
        _recent.Enqueue((trade.Size, isBuy));
        if (isBuy) _recentBuy += trade.Size; else _recentSell += trade.Size;
        _recentTotal += trade.Size;
        while (_recent.Count > RecentWindow)
        {
            var old = _recent.Dequeue();
            if (old.IsBuy) _recentBuy -= old.Volume; else _recentSell -= old.Volume;
            _recentTotal -= old.Volume;
        }

        // Trend window.
        _trend.Enqueue((trade.Size, isBuy));
        if (isBuy) _trendBuy += trade.Size; else _trendSell += trade.Size;
        _trendTotal += trade.Size;
        while (_trend.Count > TrendWindow)
        {
            var old = _trend.Dequeue();
            if (old.IsBuy) _trendBuy -= old.Volume; else _trendSell -= old.Volume;
            _trendTotal -= old.Volume;
        }
    }

    /// <summary>Aggressor ratio over the RECENT window ∈ [0, 1]. 0.5 = balanced.</summary>
    public double AggressorRatio => _recentTotal > 0 ? (double)_recentBuy / _recentTotal : 0.5;

    /// <summary>CVD imbalance over the TREND window ∈ [-1, +1].</summary>
    public double CvdImbalance => _trendTotal > 0 ? (double)(_trendBuy - _trendSell) / _trendTotal : 0;

    /// <summary>Size ratio: recent-window mean trade size / baseline mean trade size. &gt; 1 ⇒
    /// recent trades are larger than baseline ("institutional-sized" prints).</summary>
    public double SizeRatio
    {
        get
        {
            if (_baseline.Count == 0 || _baselineSizeSum <= 0 || _recent.Count == 0 || _recentTotal <= 0) return 1.0;
            var recentAvg = (double)_recentTotal / _recent.Count;
            var baseAvg = (double)_baselineSizeSum / _baseline.Count;
            return recentAvg / baseAvg;
        }
    }
}
