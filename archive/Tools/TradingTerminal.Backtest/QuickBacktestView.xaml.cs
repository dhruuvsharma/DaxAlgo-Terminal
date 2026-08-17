using System.Windows.Controls;

namespace TradingTerminal.Backtest;

public partial class QuickBacktestView : UserControl
{
    public QuickBacktestView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is QuickBacktestViewModel oldVm)
            oldVm.EquityCurveUpdated -= OnEquityCurveUpdated;
        if (e.NewValue is QuickBacktestViewModel newVm)
            newVm.EquityCurveUpdated += OnEquityCurveUpdated;
    }

    private void OnEquityCurveUpdated(object? sender, EventArgs e)
    {
        if (DataContext is not QuickBacktestViewModel vm) return;

        EquityPlot.Plot.Clear();
        if (vm.EquityCurve.Count >= 2)
        {
            var xs = vm.EquityCurve.Select(p => p.TimestampUtc.ToOADate()).ToArray();
            var ys = vm.EquityCurve.Select(p => p.Equity).ToArray();
            var line = EquityPlot.Plot.Add.Scatter(xs, ys);
            line.LineWidth = 2;
            EquityPlot.Plot.Axes.DateTimeTicksBottom();
            EquityPlot.Plot.Axes.AutoScale();
        }
        EquityPlot.Refresh();
    }
}
