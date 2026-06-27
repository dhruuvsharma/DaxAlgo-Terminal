using Avalonia.Controls;
using ScottPlot;

namespace TradingTerminal.Strategies.OrderFlowSurfaceSpike.AvaloniaUi;

/// <summary>
/// Avalonia (cross-platform) view for the Order Flow Surface Spike — net9.0-leg counterpart to the
/// WPF HelixToolkit height-map window. Renders the VM's [slice × price-bin] Z-score grid
/// (<c>double[,] Surface</c>) as a ScottPlot.Avalonia heatmap. Leak-safe: subscribes to SurfaceChanged
/// while attached and detaches on close; the VM is disposed by the shell on window close.
/// </summary>
public partial class OrderFlowSurfaceSpikeAvaloniaWindow : Window
{
    private OrderFlowSurfaceSpikeViewModel? _vm;

    public OrderFlowSurfaceSpikeAvaloniaWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rebind();
        DetachedFromVisualTree += (_, _) => Unbind();
    }

    private void Rebind()
    {
        Unbind();
        _vm = DataContext as OrderFlowSurfaceSpikeViewModel;
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
        plot.XLabel("Slice");
        plot.YLabel("Price bin");
        SurfacePlot.Refresh();
    }
}
