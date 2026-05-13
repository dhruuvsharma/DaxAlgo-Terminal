using ScottPlot;
using ScottPlot.WPF;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.Bollinger;

public partial class BollingerStrategyWindow : StrategyWindowBase
{
    public BollingerStrategyWindow() { InitializeComponent(); }

    protected override IEnumerable<WpfPlot> ChartHosts => new[] { PricePlot };

    protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase baseVm)
    {
        if (baseVm is not BollingerStrategyViewModel vm) return;
        var plot = PricePlot.Plot;
        plot.Clear();

        var bars = vm.Bars;
        if (bars.Count == 0) { PricePlot.Refresh(); return; }

        var xs    = BarIndicators.BarTimestamps(bars);
        var price = BarIndicators.BarCloses(bars);
        var (mean, _, upper, lower) = BarIndicators.Bollinger(bars, vm.Period, vm.EntryStd);

        var p = plot.Add.Scatter(xs, price);
        p.Color = StrategyChartHelpers.AccentColor; p.LineWidth = 2; p.MarkerStyle.IsVisible = false;

        var m = plot.Add.Scatter(xs, mean);
        m.Color = StrategyChartHelpers.MutedColor; m.LineWidth = 1; m.MarkerStyle.IsVisible = false;

        var u = plot.Add.Scatter(xs, upper);
        u.Color = StrategyChartHelpers.BearishColor; u.LineWidth = 1; u.MarkerStyle.IsVisible = false;
        u.LineStyle.Pattern = LinePattern.Dashed;

        var l = plot.Add.Scatter(xs, lower);
        l.Color = StrategyChartHelpers.BullishColor; l.LineWidth = 1; l.MarkerStyle.IsVisible = false;
        l.LineStyle.Pattern = LinePattern.Dashed;

        plot.Axes.AutoScale();
        PricePlot.Refresh();
    }
}
