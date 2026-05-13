using ScottPlot.WPF;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.Macd;

public partial class MacdStrategyWindow : StrategyWindowBase
{
    public MacdStrategyWindow() { InitializeComponent(); }

    protected override IEnumerable<WpfPlot> ChartHosts => new[] { PricePlot, IndicatorPlot };

    protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase baseVm)
    {
        if (baseVm is not MacdStrategyViewModel vm) return;
        var bars = vm.Bars;

        var priceP = PricePlot.Plot; priceP.Clear();
        var indP   = IndicatorPlot.Plot; indP.Clear();

        if (bars.Count == 0) { PricePlot.Refresh(); IndicatorPlot.Refresh(); return; }

        var xs    = BarIndicators.BarTimestamps(bars);
        var price = BarIndicators.BarCloses(bars);
        var (macd, signal) = BarIndicators.Macd(bars, vm.FastPeriod, vm.SlowPeriod, vm.SignalPeriod);

        var p = priceP.Add.Scatter(xs, price);
        p.Color = StrategyChartHelpers.AccentColor; p.LineWidth = 2; p.MarkerStyle.IsVisible = false;
        priceP.Axes.AutoScale();

        var m = indP.Add.Scatter(xs, macd);
        m.Color = StrategyChartHelpers.AccentColor; m.LineWidth = 1.5f; m.MarkerStyle.IsVisible = false;
        var s = indP.Add.Scatter(xs, signal);
        s.Color = StrategyChartHelpers.WarningColor; s.LineWidth = 1.5f; s.MarkerStyle.IsVisible = false;
        var zero = indP.Add.HorizontalLine(0, color: StrategyChartHelpers.MutedColor);
        zero.LineStyle.Pattern = ScottPlot.LinePattern.Dotted;
        indP.Axes.AutoScale();

        PricePlot.Refresh();
        IndicatorPlot.Refresh();
    }
}
