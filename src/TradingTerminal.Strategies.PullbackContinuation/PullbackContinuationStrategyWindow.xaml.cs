using ScottPlot.WPF;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.PullbackContinuation;

public partial class PullbackContinuationStrategyWindow : StrategyWindowBase
{
    public PullbackContinuationStrategyWindow() { InitializeComponent(); }

    protected override IEnumerable<WpfPlot> ChartHosts => new[] { PricePlot };

    protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase baseVm)
    {
        if (baseVm is not PullbackContinuationStrategyViewModel vm) return;
        var plot = PricePlot.Plot;
        plot.Clear();

        var bars = vm.Bars;
        if (bars.Count == 0) { PricePlot.Refresh(); return; }

        var xs    = BarIndicators.BarTimestamps(bars);
        var price = BarIndicators.BarCloses(bars);
        var trend = BarIndicators.Sma(bars, vm.TrendPeriod);

        var p = plot.Add.Scatter(xs, price);
        p.Color = StrategyChartHelpers.AccentColor; p.LineWidth = 2; p.MarkerStyle.IsVisible = false;
        var t = plot.Add.Scatter(xs, trend);
        t.Color = StrategyChartHelpers.WarningColor; t.LineWidth = 1.5f; t.MarkerStyle.IsVisible = false;

        plot.Axes.AutoScale();
        PricePlot.Refresh();
    }
}
