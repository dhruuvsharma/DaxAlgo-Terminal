using System.Windows;
using MahApps.Metro.Controls;
using TradingTerminal.UI;

namespace TradingTerminal.Heatmap;

/// <summary>Standalone window for the rolling correlation heatmap. View-only: renders the VM's
/// <see cref="HeatmapFrame"/> via the shared <see cref="HeatmapRenderer"/>.</summary>
public partial class CorrelationHeatmapWindow : MetroWindow
{
    public CorrelationHeatmapWindow()
    {
        InitializeComponent();
        StrategyChartHelpers.ConfigureDarkPlot(HeatmapPlot, dateTimeBottom: false);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is CorrelationHeatmapViewModel oldVm)
            oldVm.HeatmapUpdated -= OnHeatmapUpdated;
        if (e.NewValue is CorrelationHeatmapViewModel newVm)
            newVm.HeatmapUpdated += OnHeatmapUpdated;
    }

    private void OnHeatmapUpdated(object? sender, EventArgs e)
    {
        if (DataContext is CorrelationHeatmapViewModel vm)
            HeatmapRenderer.Render(HeatmapPlot, vm.CurrentFrame);
    }
}
