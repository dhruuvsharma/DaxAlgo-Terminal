using System.Windows.Controls;
using TradingTerminal.UI.Converters;

namespace TradingTerminal.LseBacktest;

public partial class LseBacktestView : UserControl
{
    public LseBacktestView()
    {
        // Register the shared strategy-tags converter in Application.Current.Resources before XAML
        // realises the strategy ComboBox item template — same pattern as BacktestView / InstrumentPicker.
        StrategyDataRequirementConverter.EnsureConverterRegistered();
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is LseBacktestViewModel oldVm)
            oldVm.EquityCurveUpdated -= OnEquityCurveUpdated;
        if (e.NewValue is LseBacktestViewModel newVm)
            newVm.EquityCurveUpdated += OnEquityCurveUpdated;
    }

    private void OnEquityCurveUpdated(object? sender, EventArgs e)
    {
        if (DataContext is not LseBacktestViewModel vm) return;

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
