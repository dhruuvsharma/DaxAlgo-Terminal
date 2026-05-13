using ScottPlot.WPF;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.AnomalyDetector;

public partial class AnomalyDetectorStrategyWindow : StrategyWindowBase
{
    public AnomalyDetectorStrategyWindow() { InitializeComponent(); }

    protected override IEnumerable<WpfPlot> ChartHosts => new[] { IndicatorPlot };

    protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase baseVm)
    {
        if (baseVm is not AnomalyDetectorStrategyViewModel vm) return;
        var plot = IndicatorPlot.Plot;
        plot.Clear();

        var bars = vm.Bars;
        if (bars.Count == 0) { IndicatorPlot.Refresh(); return; }

        var xs = BarIndicators.BarTimestamps(bars);
        var z  = BarIndicators.ZScore(bars, Math.Min(vm.Window, bars.Count));

        var s = plot.Add.Scatter(xs, z);
        s.Color = StrategyChartHelpers.AccentColor; s.LineWidth = 1.5f; s.MarkerStyle.IsVisible = false;

        var hi = plot.Add.HorizontalLine(vm.ZScoreThreshold, color: StrategyChartHelpers.BearishColor);
        hi.LineStyle.Pattern = ScottPlot.LinePattern.Dashed; hi.Text = $"+{vm.ZScoreThreshold:F1}";
        var lo = plot.Add.HorizontalLine(-vm.ZScoreThreshold, color: StrategyChartHelpers.BullishColor);
        lo.LineStyle.Pattern = ScottPlot.LinePattern.Dashed; lo.Text = $"-{vm.ZScoreThreshold:F1}";
        var zero = plot.Add.HorizontalLine(0, color: StrategyChartHelpers.MutedColor);
        zero.LineStyle.Pattern = ScottPlot.LinePattern.Dotted;

        plot.Axes.AutoScale();
        IndicatorPlot.Refresh();
    }
}
