using System.Windows;
using TradingTerminal.UI.Controls;
using System.Windows.Controls;
using TradingTerminal.UI;

namespace TradingTerminal.Ml.Stationarity;

/// <summary>
/// Code-behind for the Stationarity tool. View-only rendering: on the VM's
/// <see cref="StationarityViewModel.Updated"/> event it redraws the two ScottPlot charts —
/// the transformed series with rolling mean ± 2σ bands, and the ACF bars with the white-noise
/// confidence band. No business logic lives here (tests + transforms happen in the VM / Core).
/// </summary>
public partial class StationarityView : UserControl
{
    private static readonly ScottPlot.Color SeriesColor = new(235, 235, 235);
    private static readonly ScottPlot.Color MeanColor = new(66, 165, 245);
    private static readonly ScottPlot.Color BandColor = new(66, 165, 245, 90);
    private static readonly ScottPlot.Color AcfColor = new(38, 198, 218);
    private static readonly ScottPlot.Color AcfBandColor = new(255, 167, 38, 150);

    public StationarityView()
    {
        InitializeComponent();
        StrategyChartHelpers.ConfigureDarkPlot(SeriesPlot);
        StrategyChartHelpers.ConfigureDarkPlot(AcfPlot);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is StationarityViewModel oldVm)
            oldVm.Updated -= OnUpdated;
        if (e.NewValue is StationarityViewModel newVm)
            newVm.Updated += OnUpdated;
    }

    private void OnUpdated(object? sender, EventArgs e)
    {
        if (DataContext is not StationarityViewModel vm || vm.ChartData is not { } data) return;

        // ── Transformed series with rolling mean ± 2σ ────────────────────────────────────────
        SeriesPlot.Plot.Clear();
        var n = data.Values.Length;
        if (n >= 2)
        {
            var xs = new double[n];
            for (var i = 0; i < n; i++) xs[i] = data.Times[i].ToOADate();

            // Rolling band drawn first so the series reads on top. NaN-safe: only plot the
            // window-complete tail.
            var first = Array.FindIndex(data.RollingMean, v => !double.IsNaN(v));
            if (first >= 0 && n - first >= 2)
            {
                var bx = new double[n - first];
                var upper = new double[n - first];
                var lower = new double[n - first];
                var mean = new double[n - first];
                for (var i = first; i < n; i++)
                {
                    bx[i - first] = xs[i];
                    mean[i - first] = data.RollingMean[i];
                    upper[i - first] = data.RollingMean[i] + 2 * data.RollingStd[i];
                    lower[i - first] = data.RollingMean[i] - 2 * data.RollingStd[i];
                }

                var fill = SeriesPlot.Plot.Add.FillY(bx, lower, upper);
                fill.FillStyle.Color = new ScottPlot.Color(66, 165, 245, 28);
                fill.LineStyle.Width = 0;

                var meanLine = SeriesPlot.Plot.Add.Scatter(bx, mean);
                meanLine.LineWidth = 1.2f;
                meanLine.MarkerSize = 0;
                meanLine.Color = MeanColor;

                var upperLine = SeriesPlot.Plot.Add.Scatter(bx, upper);
                upperLine.LineWidth = 0.8f;
                upperLine.MarkerSize = 0;
                upperLine.Color = BandColor;

                var lowerLine = SeriesPlot.Plot.Add.Scatter(bx, lower);
                lowerLine.LineWidth = 0.8f;
                lowerLine.MarkerSize = 0;
                lowerLine.Color = BandColor;
            }

            var series = SeriesPlot.Plot.Add.Scatter(xs, data.Values);
            series.LineWidth = 1.4f;
            series.MarkerSize = 0;
            series.Color = SeriesColor;

            SeriesPlot.Plot.Axes.DateTimeTicksBottom();
            SeriesPlot.Plot.Axes.AutoScale();
        }
        SeriesPlot.Refresh();

        // ── ACF bars with the ±1.96/√n white-noise band ──────────────────────────────────────
        AcfPlot.Plot.Clear();
        if (data.Acf.Length > 0)
        {
            var positions = new double[data.Acf.Length];
            for (var i = 0; i < data.Acf.Length; i++) positions[i] = i + 1;

            var bars = AcfPlot.Plot.Add.Bars(positions, data.Acf);
            foreach (var bar in bars.Bars)
            {
                bar.FillColor = AcfColor;
                bar.LineWidth = 0;
                bar.Size = 0.6;
            }

            var upper = AcfPlot.Plot.Add.HorizontalLine(data.AcfBand);
            upper.Color = AcfBandColor;
            upper.LinePattern = ScottPlot.LinePattern.Dashed;
            var lower = AcfPlot.Plot.Add.HorizontalLine(-data.AcfBand);
            lower.Color = AcfBandColor;
            lower.LinePattern = ScottPlot.LinePattern.Dashed;
            var zero = AcfPlot.Plot.Add.HorizontalLine(0);
            zero.Color = new ScottPlot.Color(120, 120, 120);
            zero.LineWidth = 1;

            AcfPlot.Plot.Axes.AutoScale();
            AcfPlot.Plot.Title("ACF (autocorrelation by lag)", 12);
        }
        AcfPlot.Refresh();
    }

    private void ExportPng_Click(object sender, RoutedEventArgs e) =>
        ViewExport.SavePng(this, $"ml-stationarity-{DateTime.Now:yyyyMMdd-HHmmss}");
}
