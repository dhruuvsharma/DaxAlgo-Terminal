using ScottPlot.WPF;
using TradingTerminal.UI;
using Engine = TradingTerminal.Infrastructure.Backtest.Strategies;

namespace TradingTerminal.Strategies.ApexScalper;

public partial class ApexScalperStrategyWindow : StrategyWindowBase
{
    public ApexScalperStrategyWindow() { InitializeComponent(); }

    protected override IEnumerable<WpfPlot> ChartHosts => new[]
    {
        PricePlot, CompositePlot,
        DeltaPlot, VpinPlot,
        ObiShallowPlot, ObiDeepPlot,
        FootprintPlot, AbsorptionPlot,
        HvpPlot, TapeSpeedPlot,
    };

    protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase baseVm)
    {
        if (baseVm is not ApexScalperStrategyViewModel vm) return;
        var history = vm.EngineStrategy?.History;
        var maxN = Math.Max(10, vm.MaxChartCandles);

        // The strategy keeps an internal-candle ring; trim to the user-requested tail.
        var tail = history is null || history.Count == 0
            ? Array.Empty<Engine.ApexSnapshot>()
            : history.Skip(Math.Max(0, history.Count - maxN)).ToArray();

        DrawPrice(tail);
        DrawSeries(CompositePlot, tail, s => s.Composite, signed: true);
        DrawSeries(DeltaPlot, tail, s => s.Delta.Score, signed: true);
        DrawSeries(VpinPlot, tail, s => s.Vpin.Score, signed: true);
        DrawSeries(ObiShallowPlot, tail, s => s.ObiShallow.Score, signed: true);
        DrawSeries(ObiDeepPlot, tail, s => s.ObiDeep.Score, signed: true);
        DrawSeries(FootprintPlot, tail, s => s.Footprint.Score, signed: true);
        DrawSeries(AbsorptionPlot, tail, s => s.Absorption.Score, signed: true);
        DrawSeries(HvpPlot, tail, s => s.Hvp.Score, signed: true);
        DrawSeries(TapeSpeedPlot, tail, s => s.TapeSpeed.Score, signed: true);
    }

    private void DrawPrice(IReadOnlyList<Engine.ApexSnapshot> tail)
    {
        var plot = PricePlot.Plot;
        plot.Clear();
        if (tail.Count == 0) { PricePlot.Refresh(); return; }

        var xs = new double[tail.Count];
        var ys = new double[tail.Count];
        for (var i = 0; i < tail.Count; i++)
        {
            xs[i] = tail[i].TimestampUtc.ToOADate();
            ys[i] = tail[i].Mid;
        }
        var s = plot.Add.Scatter(xs, ys);
        s.Color = StrategyChartHelpers.AccentColor;
        s.LineWidth = 2;
        s.MarkerStyle.IsVisible = false;
        plot.Axes.DateTimeTicksBottom();
        plot.Axes.AutoScale();
        PricePlot.Refresh();
    }

    /// <summary>Draws a single time-series. When <paramref name="signed"/> is true,
    /// adds a dotted zero-line and shades positive/negative regions.</summary>
    private static void DrawSeries(WpfPlot host, IReadOnlyList<Engine.ApexSnapshot> tail,
        Func<Engine.ApexSnapshot, double> pick, bool signed)
    {
        var plot = host.Plot;
        plot.Clear();
        if (tail.Count == 0) { host.Refresh(); return; }

        var xs = new double[tail.Count];
        var ys = new double[tail.Count];
        for (var i = 0; i < tail.Count; i++)
        {
            xs[i] = tail[i].TimestampUtc.ToOADate();
            ys[i] = pick(tail[i]);
        }
        var scatter = plot.Add.Scatter(xs, ys);
        scatter.Color = StrategyChartHelpers.AccentColor;
        scatter.LineWidth = 1.5f;
        scatter.MarkerStyle.IsVisible = false;

        if (signed)
        {
            var zero = plot.Add.HorizontalLine(0, color: StrategyChartHelpers.MutedColor);
            zero.LineStyle.Pattern = ScottPlot.LinePattern.Dotted;
        }

        plot.Axes.DateTimeTicksBottom();
        plot.Axes.AutoScale();
        host.Refresh();
    }
}
