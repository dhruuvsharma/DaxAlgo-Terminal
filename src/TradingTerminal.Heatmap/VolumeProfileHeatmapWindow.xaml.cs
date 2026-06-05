using System.Windows;
using MahApps.Metro.Controls;
using TradingTerminal.UI;

namespace TradingTerminal.Heatmap;

/// <summary>Standalone window for the volume-at-price heatmap. View-only: renders the VM's
/// <see cref="HeatmapFrame"/> via the shared <see cref="HeatmapRenderer"/>.</summary>
public partial class VolumeProfileHeatmapWindow : MetroWindow
{
    public VolumeProfileHeatmapWindow()
    {
        InitializeComponent();
        StrategyChartHelpers.ConfigureDarkPlot(HeatmapPlot, dateTimeBottom: false);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is VolumeProfileHeatmapViewModel oldVm)
            oldVm.HeatmapUpdated -= OnHeatmapUpdated;
        if (e.NewValue is VolumeProfileHeatmapViewModel newVm)
            newVm.HeatmapUpdated += OnHeatmapUpdated;
    }

    private void OnHeatmapUpdated(object? sender, EventArgs e)
    {
        if (DataContext is VolumeProfileHeatmapViewModel vm)
            HeatmapRenderer.Render(HeatmapPlot, vm.CurrentFrame);
    }
}
