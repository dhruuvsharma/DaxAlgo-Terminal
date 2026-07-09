using System.Windows;
using TradingTerminal.UI.Controls;
using System.Windows.Controls;
using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.BacktestStudio;

/// <summary>
/// Code-behind for the Studio. Pure view concern: it listens for the VM's "report ready" / "replay
/// frame" signals and (re)draws the ScottPlot surfaces. No business logic — the VM owns the run,
/// the data, and the playback cursor.
/// </summary>
public partial class BacktestStudioView : UserControl
{
    private static readonly TimeSpan BarSpan = TimeSpan.FromMinutes(1);

    private BacktestStudioViewModel? _vm;

    public BacktestStudioView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        _vm = e.NewValue as BacktestStudioViewModel;
        if (_vm is null) return;
        _vm.ReportReady += OnReportReady;
        _vm.ReplayFrameChanged += OnReplayFrameChanged;
        _vm.OptimizationReady += OnOptimizationReady;
    }

    private void Detach()
    {
        if (_vm is null) return;
        _vm.ReportReady -= OnReportReady;
        _vm.ReplayFrameChanged -= OnReplayFrameChanged;
        _vm.OptimizationReady -= OnOptimizationReady;
        _vm = null;
    }

    private void OnReportReady(object? sender, EventArgs e) => DrawEquity();

    private void OnReplayFrameChanged(object? sender, EventArgs e) => DrawReplay();

    private void OnOptimizationReady(object? sender, EventArgs e) => DrawSurface();

    private void DrawSurface()
    {
        SurfacePlot.Plot.Clear();
        var scores = _vm?.SurfaceScores;
        if (scores is not null && _vm?.SurfaceXAxis is { } xAxis && _vm.SurfaceYAxis is { } yAxis)
        {
            var heatmap = SurfacePlot.Plot.Add.Heatmap(scores);
            heatmap.Colormap = new ScottPlot.Colormaps.Viridis();
            SurfacePlot.Plot.Add.ColorBar(heatmap);
            SurfacePlot.Plot.XLabel(xAxis.Label);
            SurfacePlot.Plot.YLabel(yAxis.Label);
        }
        else
        {
            // 1 axis or >2 axes: no 2D surface — the results grid tells the story.
            SurfacePlot.Plot.Title("Enable exactly two axes for a 2D score surface");
        }
        SurfacePlot.Plot.Axes.AutoScale();
        SurfacePlot.Refresh();
    }

    private void DrawEquity()
    {
        var equity = _vm?.Report?.Equity;
        EquityPlot.Plot.Clear();
        if (equity is { Count: > 0 })
        {
            var xs = equity.Select(s => s.TimestampUtc.ToOADate()).ToArray();
            var ys = equity.Select(s => s.Equity).ToArray();
            var line = EquityPlot.Plot.Add.Scatter(xs, ys);
            line.MarkerSize = 0;
            line.Color = ScottPlot.Colors.RoyalBlue;
            EquityPlot.Plot.Axes.DateTimeTicksBottom();
        }
        EquityPlot.Plot.Axes.AutoScale();
        EquityPlot.Refresh();
    }

    private void DrawReplay()
    {
        var visual = _vm?.Report?.Visual;
        ReplayPlot.Plot.Clear();

        if (visual is { Bars.Count: > 0 })
        {
            var n = Math.Clamp(_vm!.CurrentBar, 0, visual.Bars.Count);
            if (n > 0)
            {
                var ohlcs = new List<ScottPlot.OHLC>(n);
                for (var i = 0; i < n; i++)
                {
                    var b = visual.Bars[i];
                    ohlcs.Add(new ScottPlot.OHLC(b.Open, b.High, b.Low, b.Close, b.TimeUtc, BarSpan));
                }
                ReplayPlot.Plot.Add.Candlestick(ohlcs);

                var cutoff = visual.Bars[n - 1].TimeUtc;
                AddMarkers(visual.Markers, isEntry: true, cutoff, ScottPlot.Colors.Green);
                AddMarkers(visual.Markers, isEntry: false, cutoff, ScottPlot.Colors.Red);

                ReplayPlot.Plot.Axes.DateTimeTicksBottom();
            }
        }

        ReplayPlot.Plot.Axes.AutoScale();
        ReplayPlot.Refresh();
    }

    private void AddMarkers(IReadOnlyList<TradeMarker> markers, bool isEntry, DateTime cutoff, ScottPlot.Color color)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var m in markers)
        {
            if (m.IsEntry != isEntry || m.TimeUtc > cutoff) continue;
            xs.Add(m.TimeUtc.ToOADate());
            ys.Add(m.Price);
        }
        if (xs.Count == 0) return;

        var scatter = ReplayPlot.Plot.Add.Scatter(xs.ToArray(), ys.ToArray());
        scatter.LineWidth = 0;
        scatter.MarkerSize = 9;
        scatter.Color = color;
    }

    private void ExportPng_Click(object sender, RoutedEventArgs e) =>
        ViewExport.SavePng(this, $"studio-{DateTime.Now:yyyyMMdd-HHmmss}");
}
