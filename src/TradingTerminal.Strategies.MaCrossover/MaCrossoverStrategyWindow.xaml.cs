using ScottPlot.WPF;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.MaCrossover;

public partial class MaCrossoverStrategyWindow : StrategyWindowBase
{
    public MaCrossoverStrategyWindow() { InitializeComponent(); }

    protected override IEnumerable<WpfPlot> ChartHosts => new[] { PricePlot };

    protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase baseVm)
    {
        if (baseVm is not MaCrossoverStrategyViewModel vm) return;
        var plot = PricePlot.Plot;
        plot.Clear();

        var bars = vm.Bars;
        if (bars.Count == 0) { PricePlot.Refresh(); return; }

        var xs    = BarIndicators.BarTimestamps(bars);
        var price = BarIndicators.BarCloses(bars);
        var fast  = BarIndicators.Ema(bars, vm.FastPeriod);
        var slow  = BarIndicators.Ema(bars, vm.SlowPeriod);

        var p = plot.Add.Scatter(xs, price);
        p.Color = StrategyChartHelpers.AccentColor; p.LineWidth = 2; p.MarkerStyle.IsVisible = false;
        var f = plot.Add.Scatter(xs, fast);
        f.Color = StrategyChartHelpers.BullishColor; f.LineWidth = 1.5f; f.MarkerStyle.IsVisible = false;
        var s = plot.Add.Scatter(xs, slow);
        s.Color = StrategyChartHelpers.BearishColor; s.LineWidth = 1.5f; s.MarkerStyle.IsVisible = false;

        plot.Axes.AutoScale();
        PricePlot.Refresh();
    }
}
