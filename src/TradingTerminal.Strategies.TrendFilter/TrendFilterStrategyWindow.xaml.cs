using ScottPlot.WPF;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.TrendFilter;

public partial class TrendFilterStrategyWindow : StrategyWindowBase
{
    public TrendFilterStrategyWindow() { InitializeComponent(); }

    protected override IEnumerable<WpfPlot> ChartHosts => new[] { PricePlot };

    protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase baseVm)
    {
        if (baseVm is not TrendFilterStrategyViewModel vm) return;
        var plot = PricePlot.Plot;
        plot.Clear();

        var bars = vm.Bars;
        if (bars.Count == 0) { PricePlot.Refresh(); return; }

        var xs    = BarIndicators.BarTimestamps(bars);
        var price = BarIndicators.BarCloses(bars);
        var sma   = BarIndicators.Sma(bars, vm.Period);

        var p = plot.Add.Scatter(xs, price);
        p.Color = StrategyChartHelpers.AccentColor; p.LineWidth = 2; p.MarkerStyle.IsVisible = false;
        var s = plot.Add.Scatter(xs, sma);
        s.Color = StrategyChartHelpers.WarningColor; s.LineWidth = 1.5f; s.MarkerStyle.IsVisible = false;

        plot.Axes.AutoScale();
        PricePlot.Refresh();
    }
}
