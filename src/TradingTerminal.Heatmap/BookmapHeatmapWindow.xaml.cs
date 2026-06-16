using System.Windows;
using MahApps.Metro.Controls;

namespace TradingTerminal.Heatmap;

/// <summary>
/// Hosts the combined <b>Bookmap + VolBook</b> view. Pure presentation: it binds the
/// <see cref="BookmapHeatmapViewModel"/> to the <see cref="BookmapSurface"/> (which does all the
/// immediate-mode drawing) and forwards the VM's <see cref="BookmapHeatmapViewModel.HeatmapUpdated"/>
/// ticks to it. No business logic here (MVVM).
/// </summary>
public partial class BookmapHeatmapWindow : MetroWindow
{
    private readonly BookmapSurface _surface = new();
    private BookmapHeatmapViewModel? _vm;

    public BookmapHeatmapWindow()
    {
        InitializeComponent();
        SurfaceHost.Child = _surface;
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.HeatmapUpdated -= OnHeatmapUpdated;
        _vm = e.NewValue as BookmapHeatmapViewModel;
        _surface.ViewModel = _vm;
        if (_vm is not null) _vm.HeatmapUpdated += OnHeatmapUpdated;
    }

    private void OnHeatmapUpdated(object? sender, EventArgs e) => _surface.OnDataUpdated();

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        _vm.HeatmapUpdated -= OnHeatmapUpdated;
        _vm.Dispose();
    }
}
