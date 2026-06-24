using ScottPlot.WPF;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.OrnsteinUhlenbeck;

public partial class OrnsteinUhlenbeckStrategyWindow : StrategyWindowBase
{
    public OrnsteinUhlenbeckStrategyWindow() { InitializeComponent(); }

    protected override IEnumerable<WpfPlot> ChartHosts => new[] { IndicatorPlot };

    protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase baseVm)
    {
        if (baseVm is not OrnsteinUhlenbeckStrategyViewModel vm) return;
        var plot = IndicatorPlot.Plot;
        plot.Clear();

        var bars = vm.Bars;
        if (bars.Count == 0) { IndicatorPlot.Refresh(); return; }

        var window = Math.Max(20, Math.Min(vm.Lookback, bars.Count));
        var xs = BarIndicators.BarTimestamps(bars);
        var z  = BarIndicators.ZScore(bars, window);

        var s = plot.Add.Scatter(xs, z);
        s.Color = StrategyChartHelpers.AccentColor; s.LineWidth = 1.5f; s.MarkerStyle.IsVisible = false;

        AddHLine(plot,  vm.EntryZ, StrategyChartHelpers.BearishColor, $"+entry {vm.EntryZ:F2}");
        AddHLine(plot, -vm.EntryZ, StrategyChartHelpers.BullishColor, $"-entry {vm.EntryZ:F2}");
        AddHLine(plot,  vm.ExitZ,  StrategyChartHelpers.MutedColor,   $"+exit {vm.ExitZ:F2}");
        AddHLine(plot, -vm.ExitZ,  StrategyChartHelpers.MutedColor,   $"-exit {vm.ExitZ:F2}");
        AddHLine(plot,  vm.StopZ,  StrategyChartHelpers.WarningColor, $"+stop {vm.StopZ:F2}");
        AddHLine(plot, -vm.StopZ,  StrategyChartHelpers.WarningColor, $"-stop {vm.StopZ:F2}");

        plot.Axes.AutoScale();
        ApplyAxisControls(plot, vm, bars);
        IndicatorPlot.Refresh();
    }

    private static void AddHLine(ScottPlot.Plot plot, double y, ScottPlot.Color color, string label)
    {
        var line = plot.Add.HorizontalLine(y, color: color);
        line.LineStyle.Pattern = ScottPlot.LinePattern.Dashed;
        line.Text = label;
    }
}
