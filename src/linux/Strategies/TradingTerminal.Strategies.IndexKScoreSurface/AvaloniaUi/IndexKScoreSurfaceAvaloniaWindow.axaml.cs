using Avalonia.Controls;
using ScottPlot;

namespace TradingTerminal.Strategies.IndexKScoreSurface.AvaloniaUi;

/// <summary>
/// Avalonia (cross-platform) view for the Index K-Score Surface — net9.0-leg counterpart to the WPF
/// HelixToolkit surface window. Renders the VM's [component × time] K grid (<c>double[,] Surface</c>)
/// as a ScottPlot.Avalonia heatmap. Leak-safe: subscribes to SurfaceChanged while attached and
/// detaches on close; the VM is disposed by the shell on window close.
/// </summary>
public partial class IndexKScoreSurfaceAvaloniaWindow : Window
{
    private IndexKScoreSurfaceViewModel? _vm;

    public IndexKScoreSurfaceAvaloniaWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rebind();
        DetachedFromVisualTree += (_, _) => Unbind();
    }

    private void Rebind()
    {
        Unbind();
        _vm = DataContext as IndexKScoreSurfaceViewModel;
        if (_vm is not null)
        {
            _vm.SurfaceChanged += OnSurfaceChanged;
            Redraw();
        }
    }

    private void Unbind()
    {
        if (_vm is not null) _vm.SurfaceChanged -= OnSurfaceChanged;
        _vm = null;
    }

    private void OnSurfaceChanged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        var plot = SurfacePlot.Plot;
        plot.Clear();

        if (_vm?.Surface is { } grid && grid.Length > 0)
            plot.Add.Heatmap(grid);

        plot.FigureBackground.Color = Color.FromHex("#000000");
        plot.DataBackground.Color = Color.FromHex("#0A0A0A");
        plot.Axes.Color(Color.FromHex("#8A8A8A"));
        plot.XLabel("Time");
        plot.YLabel("Component");
        SurfacePlot.Refresh();
    }
}
