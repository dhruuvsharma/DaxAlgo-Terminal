using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ScottPlot;

namespace TradingTerminal.App.Avalonia.Charts;

/// <summary>
/// Cross-platform Charts window — ScottPlot.Avalonia candlestick chart that replaces the Windows-only
/// WebView2/TradingView view. Builds the plot from the VM's plain OHLC arrays on load and themes it to
/// the Bloomberg dark palette. Static synthetic series for now; the live hub feed lands in Step 9.
/// </summary>
public partial class ChartsWindow : Window
{
    public ChartsWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChartsViewModel vm) return;

        var plot = Plot.Plot;
        plot.Clear();

        var ohlcs = new List<OHLC>(vm.Close.Count);
        var start = DateTime.Today.AddHours(9.5);
        for (var i = 0; i < vm.Close.Count; i++)
        {
            ohlcs.Add(new OHLC
            {
                Open = vm.Open[i],
                High = vm.High[i],
                Low = vm.Low[i],
                Close = vm.Close[i],
                DateTime = start.AddMinutes(i),
                TimeSpan = TimeSpan.FromMinutes(1),
            });
        }

        plot.Add.Candlestick(ohlcs);

        // Bloomberg dark theme.
        plot.FigureBackground.Color = Color.FromHex("#000000");
        plot.DataBackground.Color = Color.FromHex("#0A0A0A");
        plot.Axes.Color(Color.FromHex("#8A8A8A"));
        plot.Axes.DateTimeTicksBottom();

        Plot.Refresh();
    }
}
