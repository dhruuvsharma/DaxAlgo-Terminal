using ScottPlot.WPF;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.VolatilityTargeted;

public partial class VolatilityTargetedStrategyWindow : StrategyWindowBase
{
    public VolatilityTargetedStrategyWindow() { InitializeComponent(); }

    protected override IEnumerable<WpfPlot> ChartHosts => new[] { IndicatorPlot };

    protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase baseVm)
    {
        if (baseVm is not VolatilityTargetedStrategyViewModel vm) return;
        var plot = IndicatorPlot.Plot;
        plot.Clear();
        var bars = vm.Bars;
        if (bars.Count == 0) { IndicatorPlot.Refresh(); return; }
        var window = Math.Max(20, Math.Min((int)vm.VolHalfLife, bars.Count));
        var xs  = BarIndicators.BarTimestamps(bars);
        var vol = BarIndicators.RealisedVol(bars, window);

        var s = plot.Add.Scatter(xs, vol);
        s.Color = StrategyChartHelpers.AccentColor; s.LineWidth = 1.5f; s.MarkerStyle.IsVisible = false;
        var target = plot.Add.HorizontalLine(vm.TargetVol, color: StrategyChartHelpers.BullishColor);
        target.LineStyle.Pattern = ScottPlot.LinePattern.Dashed; target.Text = $"Target {vm.TargetVol:F5}";

        plot.Axes.AutoScale();
        IndicatorPlot.Refresh();
    }
}
