using System.Windows;
using MahApps.Metro.Controls;
using TradingTerminal.UI;

namespace TradingTerminal.Heatmap;

/// <summary>Standalone window for the order-book imbalance heatmap. View-only: renders the VM's
/// <see cref="HeatmapFrame"/> via the shared <see cref="HeatmapRenderer"/>.</summary>
public partial class ImbalanceHeatmapWindow : MetroWindow
{
    public ImbalanceHeatmapWindow()
    {
        InitializeComponent();
        StrategyChartHelpers.ConfigureDarkPlot(HeatmapPlot, dateTimeBottom: false);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ImbalanceHeatmapViewModel oldVm)
            oldVm.HeatmapUpdated -= OnHeatmapUpdated;
        if (e.NewValue is ImbalanceHeatmapViewModel newVm)
            newVm.HeatmapUpdated += OnHeatmapUpdated;
    }

    private void OnHeatmapUpdated(object? sender, EventArgs e)
    {
        if (DataContext is ImbalanceHeatmapViewModel vm)
            HeatmapRenderer.Render(HeatmapPlot, vm.CurrentFrame);
    }
}
