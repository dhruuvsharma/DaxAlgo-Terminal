using System.Windows.Threading;
using ScottPlot.WPF;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.FilteredOrderFlow;

/// <summary>
/// Window for Filtered Order-Flow Imbalance. The OBI(T) series updates per-trade, far faster than
/// the base's per-bar <c>BarsChanged</c> redraw, so a coalesced <see cref="DispatcherTimer"/> drives
/// the plot at a fixed cadence (one redraw per tick, not per trade) — the same anti-churn pattern
/// the Apex / Cumulative-Delta windows use to keep a hot tape from flooding the renderer.
/// </summary>
public partial class FilteredOrderFlowWindow : StrategyWindowBase
{
    private readonly DispatcherTimer _renderTimer;

    public FilteredOrderFlowWindow()
    {
        InitializeComponent();
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(200) };
        _renderTimer.Tick += (_, _) => RedrawObi();
        _renderTimer.Start();
        Closed += (_, _) => _renderTimer.Stop();
    }

    protected override IEnumerable<WpfPlot> ChartHosts => new[] { ObiPlot };

    // Param edits / initial attach come through here; per-trade updates come through the timer.
    protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase vm) => RedrawObi();

    private void RedrawObi()
    {
        if (DataContext is not FilteredOrderFlowViewModel vm) return;
        var plot = ObiPlot.Plot;
        plot.Clear();

        var history = vm.ObiHistory;
        if (history.Count >= 2)
        {
            var n = history.Count;
            var xs = new double[n];
            var filt = new double[n];
            var unfilt = new double[n];
            for (var i = 0; i < n; i++)
            {
                xs[i] = history[i].TimeUtc.ToOADate();
                filt[i] = history[i].Filtered;
                unfilt[i] = history[i].Unfiltered;
            }

            // Muted thin line = unfiltered OBI(T); accent thick line = filtered OBI(T).
            var unfiltLine = plot.Add.Scatter(xs, unfilt);
            unfiltLine.Color = StrategyChartHelpers.MutedColor;
            unfiltLine.LineWidth = 1f;
            unfiltLine.MarkerStyle.IsVisible = false;

            var filtLine = plot.Add.Scatter(xs, filt);
            filtLine.Color = StrategyChartHelpers.AccentColor;
            filtLine.LineWidth = 2f;
            filtLine.MarkerStyle.IsVisible = false;
        }

        // Zero line + symmetric strong-regime entry thresholds on the 9-bin grid.
        AddHLine(plot, 0, StrategyChartHelpers.MutedColor, "0");
        var thr = StrongRegimeThreshold(vm.StrongRegime);
        AddHLine(plot, +thr, StrategyChartHelpers.BullishColor, $"+strong (≥{vm.StrongRegime})");
        AddHLine(plot, -thr, StrategyChartHelpers.BearishColor, $"−strong (≤−{vm.StrongRegime})");

        plot.Axes.SetLimitsY(-1.05, 1.05);
        if (history.Count >= 2)
            plot.Axes.SetLimitsX(history[0].TimeUtc.ToOADate(), history[^1].TimeUtc.ToOADate());
        ObiPlot.Refresh();
    }

    /// <summary>Lower OBI edge of the first "strong" bin on the nine-bin symmetric grid over [−1, 1].</summary>
    private static double StrongRegimeThreshold(int strongRegime)
    {
        const double binWidth = 2.0 / OrderFlowImbalance.RegimeCount;
        return (OrderFlowImbalance.RegimeCount / 2 + strongRegime) * binWidth - 1.0;
    }

    private static void AddHLine(ScottPlot.Plot plot, double y, ScottPlot.Color color, string label)
    {
        var line = plot.Add.HorizontalLine(y, color: color);
        line.LineStyle.Pattern = ScottPlot.LinePattern.Dashed;
        line.Text = label;
    }
}
