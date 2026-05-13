using ScottPlot.WPF;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.ConnorsRsi2;

public partial class ConnorsRsi2StrategyWindow : StrategyWindowBase
{
    public ConnorsRsi2StrategyWindow() { InitializeComponent(); }

    protected override IEnumerable<WpfPlot> ChartHosts => new[] { IndicatorPlot };

    protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase baseVm)
    {
        if (baseVm is not ConnorsRsi2StrategyViewModel vm) return;
        var plot = IndicatorPlot.Plot;
        plot.Clear();

        var bars = vm.Bars;
        if (bars.Count == 0) { IndicatorPlot.Refresh(); return; }

        var xs  = BarIndicators.BarTimestamps(bars);
        var rsi = BarIndicators.Rsi(bars, vm.RsiPeriod);

        var r = plot.Add.Scatter(xs, rsi);
        r.Color = StrategyChartHelpers.AccentColor; r.LineWidth = 2; r.MarkerStyle.IsVisible = false;

        var entry = plot.Add.HorizontalLine(vm.EntryRsi, color: StrategyChartHelpers.BullishColor);
        entry.LineStyle.Pattern = ScottPlot.LinePattern.Dashed; entry.Text = $"Entry {vm.EntryRsi:F0}";
        var exit = plot.Add.HorizontalLine(vm.ExitRsi, color: StrategyChartHelpers.BearishColor);
        exit.LineStyle.Pattern = ScottPlot.LinePattern.Dashed; exit.Text = $"Exit {vm.ExitRsi:F0}";

        plot.Axes.SetLimitsY(0, 100);
        plot.Axes.AutoScaleX();
        IndicatorPlot.Refresh();
    }
}
