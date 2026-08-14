using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.UI.Controls;

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
        ApplyPlotTheme(EquityPlot);
        ApplyPlotTheme(SurfacePlot);
        ApplyPlotTheme(ReplayPlot);
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (IsLoaded) Attach(e.NewValue as BacktestStudioViewModel);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        Attach(DataContext as BacktestStudioViewModel);

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Attach(BacktestStudioViewModel? viewModel)
    {
        Detach();
        _vm = viewModel;
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
        ApplyPlotTheme(SurfacePlot);
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
            var bullish = GetPlotColor("Bullish.Brush");
            var line = EquityPlot.Plot.Add.Scatter(xs, ys);
            line.MarkerSize = 0;
            line.LineWidth = 2;
            line.Color = bullish;
            line.FillY = true;
            line.FillYValue = ys.Min();
            line.FillYColor = bullish.WithAlpha(0.18);

            var lastPoint = EquityPlot.Plot.Add.Scatter(
                new[] { xs[^1] },
                new[] { ys[^1] });
            lastPoint.LineWidth = 0;
            lastPoint.MarkerSize = 9;
            lastPoint.MarkerShape = ScottPlot.MarkerShape.FilledCircle;
            lastPoint.Color = bullish;
            EquityPlot.Plot.Axes.DateTimeTicksBottom();
        }
        ApplyPlotTheme(EquityPlot);
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
                var candles = ReplayPlot.Plot.Add.Candlestick(ohlcs);
                candles.RisingColor = GetPlotColor("Bullish.Brush");
                candles.FallingColor = GetPlotColor("Bearish.Brush");

                var cutoff = visual.Bars[n - 1].TimeUtc;
                AddMarkers(visual.Markers, isEntry: true, cutoff, GetPlotColor("Bullish.Brush"));
                AddMarkers(visual.Markers, isEntry: false, cutoff, GetPlotColor("Bearish.Brush"));

                ReplayPlot.Plot.Axes.DateTimeTicksBottom();
            }
        }

        ApplyPlotTheme(ReplayPlot);
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

    private void ApplyPlotTheme(ScottPlot.WPF.WpfPlot plot)
    {
        var figure = GetPlotColor("Background.Primary");
        var data = GetPlotColor("Background.Surface");
        var grid = GetPlotColor("Border.Brush");
        var frame = GetPlotColor("Border.Strong");
        var text = GetPlotColor("Text.Secondary");

        plot.Plot.FigureBackground.Color = figure;
        plot.Plot.DataBackground.Color = data;
        plot.Plot.Grid.MajorLineColor = grid;
        plot.Plot.Grid.MinorLineColor = grid.WithAlpha(0.45);
        plot.Plot.Grid.MajorLineWidth = 1;
        plot.Plot.Grid.MinorLineWidth = 0.5f;
        plot.Plot.Axes.Color(text);
        plot.Plot.Axes.FrameColor(frame);
        plot.Plot.Legend.BackgroundColor = data;
        plot.Plot.Legend.FontColor = text;
        plot.Plot.Legend.OutlineColor = frame;
    }

    private ScottPlot.Color GetPlotColor(string resourceKey)
    {
        if (TryFindResource(resourceKey) is not SolidColorBrush brush)
            throw new InvalidOperationException($"Missing solid color theme resource '{resourceKey}'.");

        var color = brush.Color;
        return new ScottPlot.Color(color.R, color.G, color.B, color.A);
    }

    private void ExportPng_Click(object sender, RoutedEventArgs e) =>
        ViewExport.SavePng(this, $"studio-{DateTime.Now:yyyyMMdd-HHmmss}");
}
