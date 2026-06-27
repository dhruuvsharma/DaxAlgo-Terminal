using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using ScottPlot;

namespace TradingTerminal.Backtest.AvaloniaUi;

/// <summary>Avalonia (cross-platform) view for the Backtest tool — net9.0-leg counterpart to the WPF
/// window. Config + stats + a ScottPlot.Avalonia equity curve rebuilt as the run streams EquityCurve
/// points. Leak-safe: subscribes while attached, detaches on close.</summary>
public partial class BacktestAvaloniaWindow : Window
{
    private BacktestViewModel? _vm;

    public BacktestAvaloniaWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rebind();
        DetachedFromVisualTree += (_, _) => Unbind();
    }

    private void Rebind()
    {
        Unbind();
        _vm = DataContext as BacktestViewModel;
        if (_vm is not null)
        {
            _vm.EquityCurve.CollectionChanged += OnEquityChanged;
            Redraw();
        }
    }

    private void Unbind()
    {
        if (_vm is not null) _vm.EquityCurve.CollectionChanged -= OnEquityChanged;
        _vm = null;
    }

    private void OnEquityChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        var plot = EquityPlot.Plot;
        plot.Clear();

        if (_vm is { } vm && vm.EquityCurve.Count >= 2)
        {
            var ys = vm.EquityCurve.Select(p => p.Equity).ToArray();
            plot.Add.Signal(ys);
        }

        plot.FigureBackground.Color = Color.FromHex("#000000");
        plot.DataBackground.Color = Color.FromHex("#0A0A0A");
        plot.Axes.Color(Color.FromHex("#8A8A8A"));
        EquityPlot.Refresh();
    }
}
