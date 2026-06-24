using ScottPlot.WPF;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.OrderFlowToxicity;

public partial class OrderFlowToxicityStrategyWindow : StrategyWindowBase
{
    public OrderFlowToxicityStrategyWindow() { InitializeComponent(); }

    protected override IEnumerable<WpfPlot> ChartHosts => new[] { IndicatorPlot };

    protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase baseVm)
    {
        if (baseVm is not OrderFlowToxicityStrategyViewModel vm) return;
        var plot = IndicatorPlot.Plot;
        plot.Clear();
        var bars = vm.Bars;
        if (bars.Count == 0) { IndicatorPlot.Refresh(); return; }
        var window = Math.Max(20, Math.Min(vm.WindowTicks, bars.Count));
        var xs = BarIndicators.BarTimestamps(bars);
        var vol = BarIndicators.RealisedVol(bars, window);
        var s = plot.Add.Scatter(xs, vol);
        s.Color = StrategyChartHelpers.AccentColor; s.LineWidth = 1.5f; s.MarkerStyle.IsVisible = false;
        plot.Axes.AutoScale();
        ApplyAxisControls(plot, vm, bars);
        IndicatorPlot.Refresh();
    }
}
