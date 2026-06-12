using System.Windows;
using System.Windows.Controls;
using TradingTerminal.UI;

namespace TradingTerminal.Ml.KalmanFilter;

/// <summary>
/// Code-behind for the Kalman Filter tool. View-only rendering: on the VM's
/// <see cref="KalmanFilterViewModel.Updated"/> event it redraws the state chart (price +
/// filtered level, or the β path in pairs mode) and the standardized-innovation chart with
/// ±2 bands. No business logic lives here (the filters run in the VM / Core).
/// </summary>
public partial class KalmanFilterView : UserControl
{
    private static readonly ScottPlot.Color ObservedColor = new(235, 235, 235);
    private static readonly ScottPlot.Color StateColor = new(66, 165, 245);
    private static readonly ScottPlot.Color ZColor = new(38, 198, 218);
    private static readonly ScottPlot.Color BandColor = new(255, 167, 38, 150);

    public KalmanFilterView()
    {
        InitializeComponent();
        StrategyChartHelpers.ConfigureDarkPlot(StatePlot);
        StrategyChartHelpers.ConfigureDarkPlot(InnovationPlot);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is KalmanFilterViewModel oldVm)
            oldVm.Updated -= OnUpdated;
        if (e.NewValue is KalmanFilterViewModel newVm)
            newVm.Updated += OnUpdated;
    }

    private void OnUpdated(object? sender, EventArgs e)
    {
        if (DataContext is not KalmanFilterViewModel vm || vm.ChartData is not { } data) return;

        var n = data.Times.Length;
        var xs = new double[n];
        for (var i = 0; i < n; i++) xs[i] = data.Times[i].ToOADate();

        // ── State chart ──────────────────────────────────────────────────────────────────────
        StatePlot.Plot.Clear();
        if (n >= 2)
        {
            if (!data.IsPairs && data.Observed.Length == n)
            {
                var observed = StatePlot.Plot.Add.Scatter(xs, data.Observed);
                observed.LineWidth = 1.0f;
                observed.MarkerSize = 0;
                observed.Color = ObservedColor;
            }

            var state = StatePlot.Plot.Add.Scatter(xs, data.Primary);
            state.LineWidth = 1.8f;
            state.MarkerSize = 0;
            state.Color = StateColor;

            StatePlot.Plot.Axes.DateTimeTicksBottom();
            StatePlot.Plot.Axes.AutoScale();
            StatePlot.Plot.Title(data.IsPairs ? "Dynamic hedge ratio βₜ" : "Price (grey) vs filtered state (blue)", 12);
        }
        StatePlot.Refresh();

        // ── Standardized innovations with ±2 bands ───────────────────────────────────────────
        InnovationPlot.Plot.Clear();
        if (data.Z.Length >= 2)
        {
            var z = InnovationPlot.Plot.Add.Scatter(xs, data.Z);
            z.LineWidth = 1.0f;
            z.MarkerSize = 0;
            z.Color = ZColor;

            var upper = InnovationPlot.Plot.Add.HorizontalLine(2);
            upper.Color = BandColor;
            upper.LinePattern = ScottPlot.LinePattern.Dashed;
            var lower = InnovationPlot.Plot.Add.HorizontalLine(-2);
            lower.Color = BandColor;
            lower.LinePattern = ScottPlot.LinePattern.Dashed;
            var zero = InnovationPlot.Plot.Add.HorizontalLine(0);
            zero.Color = new ScottPlot.Color(120, 120, 120);
            zero.LineWidth = 1;

            InnovationPlot.Plot.Axes.DateTimeTicksBottom();
            InnovationPlot.Plot.Axes.AutoScale();
            InnovationPlot.Plot.Title(
                data.IsPairs ? "Spread innovation z-score (the pairs signal)" : "Standardized innovations (should hug ±2)", 12);
        }
        InnovationPlot.Refresh();
    }
}
