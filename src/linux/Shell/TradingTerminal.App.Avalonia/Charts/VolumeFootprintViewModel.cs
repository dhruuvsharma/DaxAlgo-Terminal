using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.App.Avalonia.Charts;

/// <summary>
/// Drives the Avalonia Volume Footprint window. Reuses the broker-neutral
/// <see cref="FootprintFeatures.BuildBar"/> aggregator from Core to turn a trade stream into
/// <see cref="FootprintBar"/>s. This first cross-platform version runs a self-contained synthetic
/// tape (random-walk mid + Lee-Ready-style buy/sell prints) so the chart renders live without a
/// broker; wiring it to the real hub trade-tape (via LiveStrategyHostServices) is the next step.
/// </summary>
public sealed partial class VolumeFootprintViewModel : ObservableObject, IDisposable
{
    private const double TickSize = 0.25;
    private const int MaxBars = 9;
    private static readonly TimeSpan BarLength = TimeSpan.FromSeconds(3);

    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private readonly List<FootprintBar> _bars = new();
    private readonly List<FootprintPrint> _current = new();
    private DateTime _barStart = DateTime.UtcNow;
    private double _mid = 5000.0;
    private long _cumDelta;

    public VolumeFootprintViewModel()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    [ObservableProperty] private IReadOnlyList<FootprintBar> _bars2 = Array.Empty<FootprintBar>();
    [ObservableProperty] private string _status = "Synthetic tape — building footprint…";

    /// <summary>Bars exposed to the FootprintControl (newest on the right).</summary>
    public IReadOnlyList<FootprintBar> Bars => Bars2;

    private void Tick()
    {
        var now = DateTime.UtcNow;

        // Emit a few synthetic prints this tick.
        int n = _rng.Next(2, 7);
        for (int i = 0; i < n; i++)
        {
            _mid += (_rng.NextDouble() - 0.5) * TickSize * 2;
            double price = Math.Round(_mid / TickSize) * TickSize;
            long size = _rng.Next(1, 25);
            var side = _rng.NextDouble() < 0.5 ? AggressorSide.Buy : AggressorSide.Sell;
            _current.Add(new FootprintPrint(price, size, side, now));
        }

        // Close the bar on schedule.
        if (now - _barStart >= BarLength && _current.Count > 0)
        {
            var bar = FootprintFeatures.BuildBar(_current, TickSize, _barStart, now,
                FeedQuality.RealTape, _cumDelta, FootprintExtractorOptions.Default);
            _cumDelta = bar.CumulativeDelta;
            _bars.Add(bar);
            while (_bars.Count > MaxBars) _bars.RemoveAt(0);
            _current.Clear();
            _barStart = now;

            Bars2 = _bars.ToArray();
            OnPropertyChanged(nameof(Bars));
            Status = $"{_bars.Count} bars · last delta {bar.Delta:+#;-#;0} · cum {bar.CumulativeDelta:+#;-#;0}";
        }
    }

    public void Dispose() => _timer.Stop();
}
