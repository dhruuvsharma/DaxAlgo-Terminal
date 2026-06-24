using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.App.Avalonia.Charts;

/// <summary>
/// Drives the Avalonia liquidity-heatmap (Bookmap) window. Maintains a rolling buffer of
/// <see cref="DepthSnapshot"/>s — the same Core type the real depth pipeline emits — fed here by a
/// self-contained synthetic book so the heatmap renders live without a broker. Wiring it to the
/// real hub depth feed (IMarketDataHub.Depth) is the next step.
/// </summary>
public sealed partial class HeatmapViewModel : ObservableObject, IDisposable
{
    private const double Tick = 0.25;
    private const int Levels = 16;
    private const int MaxColumns = 140;

    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private readonly List<DepthSnapshot> _cols = new();
    private double _mid = 5000.0;

    public HeatmapViewModel()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(160) };
        _timer.Tick += (_, _) => Tick_();
        _timer.Start();
    }

    [ObservableProperty] private IReadOnlyList<DepthSnapshot> _columns = Array.Empty<DepthSnapshot>();
    [ObservableProperty] private string _status = "Synthetic book — building liquidity heatmap…";

    private void Tick_()
    {
        _mid += (_rng.NextDouble() - 0.5) * Tick * 2;
        double bestBid = Math.Round(_mid / Tick) * Tick - Tick;
        double bestAsk = bestBid + Tick * 2;
        var bids = new List<DepthLevel>(Levels);
        var asks = new List<DepthLevel>(Levels);
        for (int i = 0; i < Levels; i++)
        {
            long baseSize = (long)(60 * Math.Exp(-i * 0.12));
            // Occasional resting "walls" to make the heatmap interesting.
            long wall = _rng.NextDouble() < 0.06 ? _rng.Next(120, 300) : 0;
            bids.Add(new DepthLevel(bestBid - i * Tick, baseSize + wall + _rng.Next(0, 25)));
            asks.Add(new DepthLevel(bestAsk + i * Tick, baseSize + (_rng.NextDouble() < 0.06 ? _rng.Next(120, 300) : 0) + _rng.Next(0, 25)));
        }
        _cols.Add(new DepthSnapshot(DateTime.UtcNow, bids, asks));
        while (_cols.Count > MaxColumns) _cols.RemoveAt(0);
        Columns = _cols.ToArray();
        Status = $"mid {_mid:0.00} · {_cols.Count}/{MaxColumns} columns · {Levels} levels/side";
    }

    public void Dispose() => _timer.Stop();
}
