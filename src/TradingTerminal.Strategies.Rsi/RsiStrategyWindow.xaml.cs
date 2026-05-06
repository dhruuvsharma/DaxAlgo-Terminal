using System.ComponentModel;
using System.Windows;
using MahApps.Metro.Controls;
using ScottPlot;
using ScottPlot.Plottables;

namespace TradingTerminal.Strategies.Rsi;

public partial class RsiStrategyWindow : MetroWindow
{
    private RsiStrategyViewModel? _vm;
    private HorizontalLine? _overboughtLine;
    private HorizontalLine? _oversoldLine;

    public RsiStrategyWindow()
    {
        InitializeComponent();
        ConfigureChart();

        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void ConfigureChart()
    {
        var plot = Plot.Plot;

        var bg   = ScottPlot.Color.FromHex("#1E1E1E");
        var grid = ScottPlot.Color.FromHex("#3F3F46");
        var text = ScottPlot.Color.FromHex("#DCDCDC");

        plot.FigureBackground.Color = bg;
        plot.DataBackground.Color   = bg;
        plot.Axes.Color(text);
        plot.Grid.MajorLineColor    = grid;
        plot.Axes.DateTimeTicksBottom();
        plot.Axes.SetLimitsY(0, 100);

        Plot.Refresh();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.BarsChanged -= OnBarsChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = e.NewValue as RsiStrategyViewModel;

        if (_vm is not null)
        {
            _vm.BarsChanged += OnBarsChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
            RedrawChart();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RsiStrategyViewModel.Overbought)
                           or nameof(RsiStrategyViewModel.Oversold))
        {
            RedrawChart();
        }
    }

    private void OnBarsChanged(object? sender, EventArgs e) => RedrawChart();

    private void RedrawChart()
    {
        if (_vm is null) return;

        var plot = Plot.Plot;
        plot.Clear();

        var rsi = _vm.RsiSeries;
        if (rsi.Length == 0)
        {
            Plot.Refresh();
            return;
        }

        var xs = new double[rsi.Length];
        for (var i = 0; i < rsi.Length; i++)
            xs[i] = _vm.Bars[i].TimestampUtc.ToOADate();

        var line = plot.Add.Scatter(xs, rsi);
        line.Color = ScottPlot.Color.FromHex("#007ACC");
        line.LineWidth = 2;
        line.MarkerStyle.IsVisible = false;

        _overboughtLine = plot.Add.HorizontalLine(_vm.Overbought, color: ScottPlot.Color.FromHex("#EF5350"));
        _overboughtLine.LineStyle.Pattern = LinePattern.Dashed;
        _overboughtLine.Text = $"OB {_vm.Overbought:F0}";
        _oversoldLine = plot.Add.HorizontalLine(_vm.Oversold, color: ScottPlot.Color.FromHex("#26A69A"));
        _oversoldLine.LineStyle.Pattern = LinePattern.Dashed;
        _oversoldLine.Text = $"OS {_vm.Oversold:F0}";

        plot.Axes.AutoScaleX();
        plot.Axes.SetLimitsY(0, 100);
        Plot.Refresh();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        _vm.BarsChanged -= OnBarsChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        await _vm.StopStreamAsync();
        _vm.Dispose();
    }
}
