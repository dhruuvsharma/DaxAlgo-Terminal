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

    /// <summary>Toolbar 📷: PNG snapshot of the whole window content (surface + read-outs).
    /// View-side by design — the visual tree is a view concern; data exports are VM commands.</summary>
    private void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (Content is not FrameworkElement root) return;
        var symbol = _vm?.SelectedInstrument?.Contract.Symbol ?? "book";
        var path = TradingTerminal.UI.Controls.ViewExport.SavePng(
            root, $"bookmap-{symbol}-{DateTime.Now:yyyyMMdd-HHmmss}");
        if (path is not null && _vm is not null) _vm.Status = $"Snapshot saved → {path}";
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        _vm.HeatmapUpdated -= OnHeatmapUpdated;
        _surface.ViewModel = null;
        _vm = null;
        // The shell window host owns and disposes the standalone view-model exactly once.
    }
}
