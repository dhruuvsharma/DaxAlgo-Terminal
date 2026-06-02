using System.Windows;
using System.Windows.Controls;
using TradingTerminal.Core.Regime.Markov;

namespace TradingTerminal.App.Regime.Markov;

/// <summary>
/// Code-behind for the Markov regime tool. View-only rendering: on the VM's
/// <see cref="MarkovRegimeViewModel.RegimeUpdated"/> event it redraws the ScottPlot chart —
/// the closing price line with a per-bar background ribbon shaded by the decoded regime. No
/// business logic lives here (the fit + labelling happen in the VM / Core).
/// </summary>
public partial class MarkovRegimeView : UserControl
{
    // Regime ribbon colours (semi-transparent so the price line reads on top).
    private static readonly ScottPlot.Color Bearish = new(255, 82, 82, 48);
    private static readonly ScottPlot.Color Bullish = new(0, 200, 83, 48);
    private static readonly ScottPlot.Color Neutral = new(158, 158, 158, 40);

    public MarkovRegimeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MarkovRegimeViewModel oldVm)
            oldVm.RegimeUpdated -= OnRegimeUpdated;
        if (e.NewValue is MarkovRegimeViewModel newVm)
            newVm.RegimeUpdated += OnRegimeUpdated;
    }

    private void OnRegimeUpdated(object? sender, EventArgs e)
    {
        if (DataContext is not MarkovRegimeViewModel vm || vm.Result is not { } result) return;

        RegimePlot.Plot.Clear();
        var series = result.Series;
        if (series.Count >= 2)
        {
            var xs = new double[series.Count];
            var ys = new double[series.Count];
            for (var i = 0; i < series.Count; i++)
            {
                xs[i] = series[i].TimeUtc.ToOADate();
                ys[i] = series[i].Close;
            }

            // Y-range for the ribbon rectangles, padded so they fully back the price line.
            double yMin = ys[0], yMax = ys[0];
            foreach (var y in ys) { if (y < yMin) yMin = y; if (y > yMax) yMax = y; }
            var pad = (yMax - yMin) * 0.05;
            if (pad <= 0) pad = Math.Abs(yMax) * 0.01 + 1e-6;
            yMin -= pad; yMax += pad;

            // Shade contiguous runs of the same decoded regime as one rectangle each.
            var runStart = 0;
            for (var i = 1; i <= series.Count; i++)
            {
                var boundary = i == series.Count || series[i].Label != series[runStart].Label;
                if (!boundary) continue;
                var left = xs[runStart];
                var right = xs[i - 1];
                if (right <= left) right = left + (xs.Length > 1 ? (xs[^1] - xs[0]) / xs.Length : 1.0);
                var rect = RegimePlot.Plot.Add.Rectangle(left, right, yMin, yMax);
                rect.FillStyle.Color = ColorFor(series[runStart].Label);
                rect.LineStyle.Width = 0;
                runStart = i;
            }

            var price = RegimePlot.Plot.Add.Scatter(xs, ys);
            price.LineWidth = 1.5f;
            price.MarkerSize = 0;
            price.Color = new ScottPlot.Color(235, 235, 235);

            RegimePlot.Plot.Axes.DateTimeTicksBottom();
            RegimePlot.Plot.Axes.AutoScale();
        }
        RegimePlot.Refresh();
    }

    private static ScottPlot.Color ColorFor(RegimeLabel label) => label switch
    {
        RegimeLabel.Bearish => Bearish,
        RegimeLabel.Bullish => Bullish,
        _ => Neutral,
    };
}
