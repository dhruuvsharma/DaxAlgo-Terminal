using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TradingTerminal.ExecutionUi;

/// <summary>
/// Owns ScottPlot presentation plumbing and clears the embedded Login forms' PasswordBoxes when
/// their in-memory credentials expire. Commands and refresh state remain in the view-model.
/// </summary>
public partial class ExecutionConsoleView : UserControl, IDisposable
{
    private ExecutionConsoleViewModel? _viewModel;
    private int _disposed;

    public ExecutionConsoleView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (IsLoaded)
            Attach(e.NewValue as ExecutionConsoleViewModel);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        Attach(DataContext as ExecutionConsoleViewModel);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Detach();
        ResetPlots();
    }

    private void Attach(ExecutionConsoleViewModel? viewModel)
    {
        Detach();
        if (Volatile.Read(ref _disposed) != 0 || viewModel is null)
            return;

        _viewModel = viewModel;
        _viewModel.ChartsInvalidated += OnChartsInvalidated;
        _viewModel.CredentialInputsCleared += OnCredentialInputsCleared;
        _viewModel.Disposing += OnViewModelDisposing;
        DrawCharts();
    }

    private void Detach()
    {
        if (_viewModel is null)
            return;

        _viewModel.ChartsInvalidated -= OnChartsInvalidated;
        _viewModel.CredentialInputsCleared -= OnCredentialInputsCleared;
        _viewModel.Disposing -= OnViewModelDisposing;
        _viewModel = null;
    }

    private void OnChartsInvalidated(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            DrawCharts();
            return;
        }

        _ = Dispatcher.BeginInvoke(DrawCharts);
    }

    private void OnViewModelDisposing(object? sender, EventArgs e)
    {
        ClearCredentialPasswordBoxes();
        Detach();
        ResetPlots();
    }

    private void OnCredentialInputsCleared(object? sender, EventArgs e) =>
        ClearCredentialPasswordBoxes();

    private void ClearCredentialPasswordBoxes()
    {
        foreach (var passwordBox in Descendants<PasswordBox>(this))
            passwordBox.Clear();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private void DrawCharts()
    {
        if (Volatile.Read(ref _disposed) != 0 || _viewModel is null)
            return;

        DrawEquity(_viewModel.EquitySeries, _viewModel.Metrics.Equity - _viewModel.Metrics.NetProfitAndLoss);
        DrawDrawdown(_viewModel.EquitySeries);
        DrawDailyProfitAndLoss(_viewModel.DailyPnlSeries);
        DrawSlippage(_viewModel.SlippageSeries);
    }

    /// <summary>Underwater curve: how far equity sits below its running peak, in percent, on the equity's x axis.</summary>
    private void DrawDrawdown(IReadOnlyList<ExecutionEquityPointReadModel> series)
    {
        DrawdownPlot.Plot.Clear();
        if (series.Count > 0)
        {
            var xs = series.Select(point => point.TimestampUtc.ToOADate()).ToArray();
            var ys = new double[series.Count];
            var peak = double.MinValue;
            for (var index = 0; index < series.Count; index++)
            {
                var equity = (double)series[index].Equity;
                peak = Math.Max(peak, equity);
                ys[index] = peak > 0d ? (equity - peak) / peak * 100d : 0d;
            }

            var bearish = GetPlotColor("Bearish.Brush");
            var curve = DrawdownPlot.Plot.Add.Scatter(xs, ys);
            curve.MarkerSize = 0;
            curve.LineWidth = 1.5f;
            curve.Color = bearish;
            curve.FillY = true;
            curve.FillYValue = 0;
            curve.FillYColor = bearish.WithAlpha(0.28);
            DrawdownPlot.Plot.Axes.DateTimeTicksBottom();
        }

        ApplyPlotTheme(DrawdownPlot);
        DrawdownPlot.Plot.Axes.AutoScale();
        DrawdownPlot.Plot.Axes.SetLimitsY(Math.Min(-0.5, DrawdownPlot.Plot.Axes.GetLimits().Bottom), 0.2);
        DrawdownPlot.Refresh();
    }

    /// <summary>Histogram of per-fill slippage in basis points: green bars saved money, red ones cost it.</summary>
    private void DrawSlippage(IReadOnlyList<double> series)
    {
        SlippagePlot.Plot.Clear();
        var reach = 2d;
        if (series.Count > 0)
        {
            // Bins centred on whole multiples of the width, so zero slippage is its own bar at zero.
            var span = Math.Max(series.Max() - series.Min(), 1d);
            var width = span <= 6d ? 0.5d : span <= 20d ? 1d : Math.Ceiling(span / 20d);
            var counts = new SortedDictionary<double, int>();
            foreach (var value in series)
            {
                var centre = Math.Round(value / width) * width;
                counts[centre] = counts.TryGetValue(centre, out var count) ? count + 1 : 1;
            }

            var bullish = GetPlotColor("Bullish.Brush");
            var bearish = GetPlotColor("Bearish.Brush");
            var neutral = GetPlotColor("Text.Secondary");
            var bars = counts
                .Select(pair => new ScottPlot.Bar
                {
                    Position = pair.Key,
                    Value = pair.Value,
                    Size = width * 0.8d,
                    FillColor = pair.Key > 0d ? bearish : pair.Key < 0d ? bullish : neutral,
                    LineWidth = 0,
                })
                .ToArray();
            SlippagePlot.Plot.Add.Bars(bars);
            SlippagePlot.Plot.Add.VerticalLine(0, 1, GetPlotColor("Border.Strong"));
            reach = Math.Max(reach, Math.Max(Math.Abs(counts.Keys.First()), Math.Abs(counts.Keys.Last())) + width);
        }

        ApplyPlotTheme(SlippagePlot);
        SlippagePlot.Plot.Axes.Bottom.TickGenerator = InvariantTicks();
        SlippagePlot.Plot.Axes.AutoScale();
        SlippagePlot.Plot.Axes.SetLimitsX(-reach, reach);
        SlippagePlot.Plot.Axes.SetLimitsY(0, Math.Max(1d, SlippagePlot.Plot.Axes.GetLimits().Top));
        SlippagePlot.Refresh();
    }

    /// <summary>Cumulative realized P&amp;L over the period: equity less where the period started, so a few dollars
    /// on a large account still reads as a line rather than a flat one at the account's size.</summary>
    private void DrawEquity(IReadOnlyList<ExecutionEquityPointReadModel> series, decimal equityAtStart)
    {
        EquityPlot.Plot.Clear();
        if (series.Count > 0)
        {
            var xs = series.Select(point => point.TimestampUtc.ToOADate()).ToArray();
            var ys = series.Select(point => (double)(point.Equity - equityAtStart)).ToArray();
            var accent = GetPlotColor("Accent.Brush");

            var curve = EquityPlot.Plot.Add.Scatter(xs, ys);
            curve.MarkerSize = 0;
            curve.LineWidth = 2;
            curve.Color = accent;
            curve.FillY = true;
            curve.FillYValue = 0;
            curve.FillYColor = accent.WithAlpha(0.18);
            EquityPlot.Plot.Add.HorizontalLine(0, 1, GetPlotColor("Border.Strong"));

            var endpoint = EquityPlot.Plot.Add.Scatter(new[] { xs[^1] }, new[] { ys[^1] });
            endpoint.LineWidth = 0;
            endpoint.MarkerSize = 8;
            endpoint.MarkerShape = ScottPlot.MarkerShape.FilledCircle;
            endpoint.Color = GetPlotColor("Text.Highlight");
            EquityPlot.Plot.Axes.DateTimeTicksBottom();
        }

        ApplyPlotTheme(EquityPlot);
        EquityPlot.Plot.Axes.AutoScale();
        EquityPlot.Refresh();
    }

    private void DrawDailyProfitAndLoss(IReadOnlyList<ExecutionDailyPnlPointReadModel> series)
    {
        DailyPnlPlot.Plot.Clear();
        if (series.Count > 0)
        {
            var bullish = GetPlotColor("Bullish.Brush");
            var bearish = GetPlotColor("Bearish.Brush");
            var neutral = GetPlotColor("Text.Secondary");
            var bars = series
                .Select(point => new ScottPlot.Bar
                {
                    Position = point.DateUtc.ToOADate(),
                    Value = (double)point.RealizedProfitAndLoss,
                    FillColor = point.RealizedProfitAndLoss switch
                    {
                        > 0m => bullish,
                        < 0m => bearish,
                        _ => neutral,
                    },
                    LineWidth = 0,
                })
                .ToArray();

            DailyPnlPlot.Plot.Add.Bars(bars);
            DailyPnlPlot.Plot.Add.HorizontalLine(0, 1, GetPlotColor("Border.Strong"));
            DailyPnlPlot.Plot.Axes.DateTimeTicksBottom();
        }

        ApplyPlotTheme(DailyPnlPlot);
        DailyPnlPlot.Plot.Axes.AutoScale();
        DailyPnlPlot.Refresh();
    }

    private void ApplyPlotTheme(ScottPlot.WPF.WpfPlot plot)
    {
        // Charts sit in inset panels on the primary background; the plot area matches so the chart reads as
        // part of its panel rather than a box inside it.
        var figure = GetPlotColor("Background.Primary");
        var data = GetPlotColor("Background.Primary");
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
        plot.Plot.Axes.Left.TickGenerator = InvariantTicks();
    }

    /// <summary>Value ticks as a desk writes them, whatever the machine's culture: 1,250.5 rather than 1,250.5 in
    /// one locale and 1.250,5 or 1,25,000 in another.</summary>
    private static ScottPlot.TickGenerators.NumericAutomatic InvariantTicks() => new()
    {
        LabelFormatter = value => value.ToString("#,##0.#####", System.Globalization.CultureInfo.InvariantCulture),
    };

    private ScottPlot.Color GetPlotColor(string resourceKey)
    {
        if (TryFindResource(resourceKey) is not SolidColorBrush brush)
            throw new InvalidOperationException($"Missing solid color theme resource '{resourceKey}'.");

        var color = brush.Color;
        return new ScottPlot.Color(color.R, color.G, color.B, color.A);
    }

    private void ResetPlots()
    {
        // WpfPlot.Reset disposes the old ScottPlot.Plot before installing an empty replacement.
        EquityPlot.Reset();
        DrawdownPlot.Reset();
        DailyPnlPlot.Reset();
        SlippagePlot.Reset();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Detach();
        DataContextChanged -= OnDataContextChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        ClearCredentialPasswordBoxes();
        ResetPlots();
    }
}
