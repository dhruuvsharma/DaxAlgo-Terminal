using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TradingTerminal.App.Avalonia.Charts;

/// <summary>
/// VM for the cross-platform Charts window — the ScottPlot.Avalonia replacement for the Windows-only
/// WebView2/TradingView chart. Exposes plain OHLC arrays (no ScottPlot types) that the window maps
/// into a candlestick plot. Synthetic random-walk series for now (same as the other ported charts);
/// live hub wiring lands in the Step-9 sweep.
/// </summary>
public sealed partial class ChartsViewModel : ObservableObject
{
    [ObservableProperty] private string _symbol = "SPY";

    /// <summary>Synthetic OHLC bars: parallel arrays so the view stays free of ScottPlot types.</summary>
    public IReadOnlyList<double> Open { get; }
    public IReadOnlyList<double> High { get; }
    public IReadOnlyList<double> Low { get; }
    public IReadOnlyList<double> Close { get; }

    public ChartsViewModel()
    {
        const int n = 120;
        var open = new double[n];
        var high = new double[n];
        var low = new double[n];
        var close = new double[n];

        var rng = new Random(7);
        var price = 450.0;
        for (var i = 0; i < n; i++)
        {
            var o = price;
            var drift = (rng.NextDouble() - 0.48) * 2.2;
            var c = Math.Max(1, o + drift);
            var hi = Math.Max(o, c) + rng.NextDouble() * 1.1;
            var lo = Math.Min(o, c) - rng.NextDouble() * 1.1;
            open[i] = o; close[i] = c; high[i] = hi; low[i] = lo;
            price = c;
        }

        Open = open; High = high; Low = low; Close = close;
    }
}
