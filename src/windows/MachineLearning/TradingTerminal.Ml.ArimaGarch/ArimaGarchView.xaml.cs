using System.Windows;
using TradingTerminal.UI.Controls;
using System.Windows.Controls;
using TradingTerminal.UI;

namespace TradingTerminal.Ml.ArimaGarch;

/// <summary>
/// Code-behind for the ARIMA &amp; GARCH tool. View-only rendering: on the VM's
/// <see cref="ArimaGarchViewModel.Updated"/> event it redraws the forecast chart (price history,
/// point forecast, 95% band) and the conditional-volatility chart. No business logic lives here
/// (fitting + forecasting happen in the VM / Core).
/// </summary>
public partial class ArimaGarchView : UserControl
{
    private static readonly ScottPlot.Color PriceColor = new(235, 235, 235);
    private static readonly ScottPlot.Color ForecastColor = new(66, 165, 245);
    private static readonly ScottPlot.Color BandFill = new(66, 165, 245, 40);
    private static readonly ScottPlot.Color VolColor = new(255, 167, 38);
    private static readonly ScottPlot.Color LongRunColor = new(158, 158, 158);

    public ArimaGarchView()
    {
        InitializeComponent();
        StrategyChartHelpers.ConfigureDarkPlot(ForecastPlot);
        StrategyChartHelpers.ConfigureDarkPlot(VolPlot);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ArimaGarchViewModel oldVm)
            oldVm.Updated -= OnUpdated;
        if (e.NewValue is ArimaGarchViewModel newVm)
            newVm.Updated += OnUpdated;
    }

    private void OnUpdated(object? sender, EventArgs e)
    {
        if (DataContext is not ArimaGarchViewModel vm || vm.ChartData is not { } data) return;

        // ── Price history + forecast band ────────────────────────────────────────────────────
        ForecastPlot.Plot.Clear();
        if (data.Closes.Length >= 2)
        {
            var xs = new double[data.Closes.Length];
            for (var i = 0; i < xs.Length; i++) xs[i] = data.Times[i].ToOADate();

            if (data.ForecastMean.Length > 0)
            {
                var fx = new double[data.ForecastMean.Length];
                for (var i = 0; i < fx.Length; i++) fx[i] = data.ForecastTimes[i].ToOADate();

                var band = ForecastPlot.Plot.Add.FillY(fx, data.ForecastLower, data.ForecastUpper);
                band.FillStyle.Color = BandFill;
                band.LineStyle.Width = 0;

                var fLine = ForecastPlot.Plot.Add.Scatter(fx, data.ForecastMean);
                fLine.LineWidth = 1.8f;
                fLine.MarkerSize = 0;
                fLine.Color = ForecastColor;
                fLine.LinePattern = ScottPlot.LinePattern.Dashed;
            }

            var price = ForecastPlot.Plot.Add.Scatter(xs, data.Closes);
            price.LineWidth = 1.4f;
            price.MarkerSize = 0;
            price.Color = PriceColor;

            ForecastPlot.Plot.Axes.DateTimeTicksBottom();
            ForecastPlot.Plot.Axes.AutoScale();
        }
        ForecastPlot.Refresh();

        // ── Conditional volatility (per-bar %, with the long-run level) ──────────────────────
        VolPlot.Plot.Clear();
        if (data.VolPct.Length >= 2)
        {
            var vx = new double[data.VolPct.Length];
            for (var i = 0; i < vx.Length; i++) vx[i] = data.VolTimes[i].ToOADate();

            var vol = VolPlot.Plot.Add.Scatter(vx, data.VolPct);
            vol.LineWidth = 1.3f;
            vol.MarkerSize = 0;
            vol.Color = VolColor;

            if (data.LongRunVolPct > 0)
            {
                var lr = VolPlot.Plot.Add.HorizontalLine(data.LongRunVolPct);
                lr.Color = LongRunColor;
                lr.LinePattern = ScottPlot.LinePattern.Dashed;
            }

            VolPlot.Plot.Axes.DateTimeTicksBottom();
            VolPlot.Plot.Axes.AutoScale();
            VolPlot.Plot.Title("GARCH(1,1) conditional volatility (% per bar)", 12);
        }
        VolPlot.Refresh();
    }

    private void ExportPng_Click(object sender, RoutedEventArgs e) =>
        ViewExport.SavePng(this, $"ml-arimagarch-{DateTime.Now:yyyyMMdd-HHmmss}");
}
