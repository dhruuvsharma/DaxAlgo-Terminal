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

        DrawEquity(_viewModel.EquitySeries);
        DrawDailyProfitAndLoss(_viewModel.DailyPnlSeries);
    }

    private void DrawEquity(IReadOnlyList<ExecutionEquityPointReadModel> series)
    {
        EquityPlot.Plot.Clear();
        if (series.Count > 0)
        {
            var xs = series.Select(point => point.TimestampUtc.ToOADate()).ToArray();
            var ys = series.Select(point => (double)point.Equity).ToArray();
            var accent = GetPlotColor("Accent.Brush");

            var curve = EquityPlot.Plot.Add.Scatter(xs, ys);
            curve.MarkerSize = 0;
            curve.LineWidth = 2;
            curve.Color = accent;
            curve.FillY = true;
            curve.FillYValue = ys.Min();
            curve.FillYColor = accent.WithAlpha(0.18);

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

    private void ResetPlots()
    {
        // WpfPlot.Reset disposes the old ScottPlot.Plot before installing an empty replacement.
        EquityPlot.Reset();
        DailyPnlPlot.Reset();
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
