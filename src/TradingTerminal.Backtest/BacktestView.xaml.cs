using System.Windows.Controls;
using TradingTerminal.UI.Converters;

namespace TradingTerminal.Backtest;

public partial class BacktestView : UserControl
{
    public BacktestView()
    {
        // Register the shared converter in Application.Current.Resources before XAML realises
        // the ComboBox item template — same pattern as InstrumentPicker.
        StrategyDataRequirementConverter.EnsureConverterRegistered();
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is BacktestViewModel oldVm)
            oldVm.EquityCurveUpdated -= OnEquityCurveUpdated;
        if (e.NewValue is BacktestViewModel newVm)
            newVm.EquityCurveUpdated += OnEquityCurveUpdated;
    }

    private void OnEquityCurveUpdated(object? sender, EventArgs e)
    {
        if (DataContext is not BacktestViewModel vm) return;

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
