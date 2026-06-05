using System.Windows;
using MahApps.Metro.Controls;
using TradingTerminal.UI;

namespace TradingTerminal.Heatmap;

/// <summary>
/// Standalone window hosting the Depth Heatmap tool. View-only: it hands the VM's
/// <see cref="HeatmapFrame"/> to the shared <see cref="HeatmapRenderer"/>. No business logic here —
/// the VM owns the buffer, frame building, and depth subscription.
/// </summary>
public partial class DepthHeatmapWindow : MetroWindow
{
    public DepthHeatmapWindow()
    {
        InitializeComponent();
        StrategyChartHelpers.ConfigureDarkPlot(HeatmapPlot, dateTimeBottom: false);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DepthHeatmapViewModel oldVm)
            oldVm.HeatmapUpdated -= OnHeatmapUpdated;
        if (e.NewValue is DepthHeatmapViewModel newVm)
            newVm.HeatmapUpdated += OnHeatmapUpdated;
    }

    private void OnHeatmapUpdated(object? sender, EventArgs e)
    {
        if (DataContext is DepthHeatmapViewModel vm)
            HeatmapRenderer.Render(HeatmapPlot, vm.CurrentFrame);
    }
}
