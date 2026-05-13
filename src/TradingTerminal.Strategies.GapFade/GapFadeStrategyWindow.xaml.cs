using ScottPlot.WPF;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.GapFade;

public partial class GapFadeStrategyWindow : StrategyWindowBase
{
    public GapFadeStrategyWindow() { InitializeComponent(); }

    protected override IEnumerable<WpfPlot> ChartHosts => new[] { PricePlot };

    protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase baseVm)
    {
        var plot = PricePlot.Plot;
        plot.Clear();
        var bars = baseVm.Bars;
        if (bars.Count == 0) { PricePlot.Refresh(); return; }
        var xs = BarIndicators.BarTimestamps(bars);
        var price = BarIndicators.BarCloses(bars);
        var p = plot.Add.Scatter(xs, price);
        p.Color = StrategyChartHelpers.AccentColor; p.LineWidth = 2; p.MarkerStyle.IsVisible = false;
        plot.Axes.AutoScale();
        PricePlot.Refresh();
    }
}
