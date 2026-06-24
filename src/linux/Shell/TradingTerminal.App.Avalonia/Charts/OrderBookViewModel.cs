using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.App.Avalonia.Charts;

/// <summary>
/// Drives the Avalonia OrderBook (DOM) window. Builds broker-neutral <see cref="DepthSnapshot"/>s —
/// the same Core type the real depth pipeline produces — from a self-contained synthetic book
/// (random-walk mid, exponentially-decaying sizes per level) so the ladder renders live without a
/// broker. Wiring it to the real hub depth feed (IMarketDataHub.Depth) is the next step.
/// </summary>
public sealed partial class OrderBookViewModel : ObservableObject, IDisposable
{
    private const double Tick = 0.25;
    private const int Levels = 12;

    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private double _mid = 5000.0;

    public OrderBookViewModel()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick_();
        _timer.Start();
        Tick_();
    }

    [ObservableProperty] private DepthSnapshot? _book;
    [ObservableProperty] private string _status = "Synthetic book — streaming depth…";

    private void Tick_()
    {
        _mid += (_rng.NextDouble() - 0.5) * Tick * 2;
        double bestBid = Math.Round(_mid / Tick) * Tick - Tick;
        double bestAsk = bestBid + Tick * 2;

        var bids = new List<DepthLevel>(Levels);
        var asks = new List<DepthLevel>(Levels);
        for (int i = 0; i < Levels; i++)
        {
            long baseSize = (long)(40 * Math.Exp(-i * 0.18));
            bids.Add(new DepthLevel(bestBid - i * Tick, baseSize + _rng.Next(0, 30)));
            asks.Add(new DepthLevel(bestAsk + i * Tick, baseSize + _rng.Next(0, 30)));
        }

        Book = new DepthSnapshot(DateTime.UtcNow, bids, asks);
        Status = $"mid {_mid:0.00} · best {bestBid:0.00} / {bestAsk:0.00} · {Levels} levels";
    }

    public void Dispose() => _timer.Stop();
}
