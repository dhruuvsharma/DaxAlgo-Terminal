using System.Windows;
using MahApps.Metro.Controls;
using ScottPlot;

namespace TradingTerminal.Strategies.CumulativeDelta;

public partial class CumulativeDeltaWindow : MetroWindow
{
    private CumulativeDeltaViewModel? _vm;

    public CumulativeDeltaWindow()
    {
        InitializeComponent();
        ConfigurePricePlot();
        ConfigureDeltaPlot();

        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void ConfigurePricePlot()
    {
        StyleDarkPlot(PricePlot.Plot);
        PricePlot.Plot.Axes.DateTimeTicksBottom();
        PricePlot.Refresh();
    }

    private void ConfigureDeltaPlot()
    {
        StyleDarkPlot(DeltaPlot.Plot);
        DeltaPlot.Plot.Axes.Bottom.IsVisible = false;
        DeltaPlot.Refresh();
    }

    private static void StyleDarkPlot(Plot plot)
    {
        var bg   = ScottPlot.Color.FromHex("#1E1E1E");
        var grid = ScottPlot.Color.FromHex("#3F3F46");
        var text = ScottPlot.Color.FromHex("#DCDCDC");
        plot.FigureBackground.Color = bg;
        plot.DataBackground.Color   = bg;
        plot.Axes.Color(text);
        plot.Grid.MajorLineColor    = grid;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.BarsChanged   -= OnBarsChanged;
            _vm.DeltasChanged -= OnDeltasChanged;
        }

        _vm = e.NewValue as CumulativeDeltaViewModel;

        if (_vm is not null)
        {
            _vm.BarsChanged   += OnBarsChanged;
            _vm.DeltasChanged += OnDeltasChanged;
            RedrawPrice();
            RedrawDeltas();
        }
    }

    private void OnBarsChanged(object? sender, EventArgs e) => RedrawPrice();
    private void OnDeltasChanged(object? sender, EventArgs e) => RedrawDeltas();

    private void RedrawPrice()
    {
        if (_vm is null) return;
        var plot = PricePlot.Plot;
        plot.Clear();

        if (_vm.Bars.Count == 0)
        {
            PricePlot.Refresh();
            return;
        }

        // "Bars shown" axis control = how many trailing bars to draw (X zoom).
        var total = _vm.Bars.Count;
        var shown = Math.Min(total, Math.Max(10, _vm.ChartBarsShown));
        var startIdx = total - shown;

        var xs = new double[shown];
        var ys = new double[shown];
        for (var i = 0; i < shown; i++)
        {
            xs[i] = _vm.Bars[startIdx + i].TimestampUtc.ToOADate();
            ys[i] = _vm.Bars[startIdx + i].Close;
        }

        var line = plot.Add.Scatter(xs, ys);
        line.Color = ScottPlot.Color.FromHex("#007ACC");
        line.LineWidth = 2;
        line.MarkerStyle.IsVisible = false;

        if (_vm.LastEmaHtf is { } ema)
        {
            var emaLine = plot.Add.HorizontalLine(ema, color: ScottPlot.Color.FromHex("#FFCA28"));
            emaLine.LineStyle.Pattern = LinePattern.Dashed;
            emaLine.Text = $"EMA15m {ema:F5}";
        }

        plot.Axes.AutoScale();
        // Manual Y scale overrides the auto Y range (X stays auto) when enabled and valid.
        if (!_vm.PriceAutoScale && _vm.PriceAxisMax > _vm.PriceAxisMin)
            plot.Axes.SetLimitsY(_vm.PriceAxisMin, _vm.PriceAxisMax);
        PricePlot.Refresh();
    }

    private void RedrawDeltas()
    {
        if (_vm is null) return;
        var plot = DeltaPlot.Plot;
        plot.Clear();

        if (_vm.BarDeltas.Count == 0)
        {
            DeltaPlot.Refresh();
            return;
        }

        // Build bar plot from window deltas. Positive bars green, negative red.
        var bars = new List<ScottPlot.Bar>(_vm.BarDeltas.Count);
        for (var i = 0; i < _vm.BarDeltas.Count; i++)
        {
            var v = _vm.BarDeltas[i];
            bars.Add(new ScottPlot.Bar
            {
                Position = i + 1,
                Value = v,
                FillColor = v >= 0
                    ? ScottPlot.Color.FromHex("#26A69A")
                    : ScottPlot.Color.FromHex("#EF5350"),
            });
        }
        plot.Add.Bars(bars);

        // ±threshold + cumDelta reference line.
        var threshLine = plot.Add.HorizontalLine(_vm.DeltaThreshold, color: ScottPlot.Color.FromHex("#26A69A"));
        threshLine.LineStyle.Pattern = LinePattern.Dotted;
        threshLine.Text = $"+{_vm.DeltaThreshold}";
        var negLine = plot.Add.HorizontalLine(-_vm.DeltaThreshold, color: ScottPlot.Color.FromHex("#EF5350"));
        negLine.LineStyle.Pattern = LinePattern.Dotted;
        negLine.Text = $"-{_vm.DeltaThreshold}";

        var cumLine = plot.Add.HorizontalLine(_vm.CumulativeDelta, color: ScottPlot.Color.FromHex("#FFCA28"));
        cumLine.LineWidth = 2;
        cumLine.Text = $"Σ {_vm.CumulativeDelta}";

        plot.Axes.AutoScale();
        DeltaPlot.Refresh();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        _vm.BarsChanged   -= OnBarsChanged;
        _vm.DeltasChanged -= OnDeltasChanged;
        await _vm.StopStreamAsync();
        _vm.Dispose();
    }
}
