using System.Collections.Specialized;
using Avalonia.Controls;
using ScottPlot;

namespace TradingTerminal.Strategies.OrderFlowCube.AvaloniaUi;

/// <summary>
/// Avalonia (cross-platform) view for the Order-Flow Cube — net9.0-leg counterpart to the WPF Helix
/// cube window. Renders the bounded phase-space trail (Aggressor × CVD) as a ScottPlot.Avalonia 2D
/// scatter. Leak-safe: subscribes to the bounded TrailPoints collection while attached and detaches
/// on close; the VM itself is disposed by the shell on window close.
/// </summary>
public partial class OrderFlowCubeAvaloniaWindow : Window
{
    private OrderFlowCubeViewModel? _vm;

    public OrderFlowCubeAvaloniaWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rebind();
        DetachedFromVisualTree += (_, _) => Unbind();
    }

    private void Rebind()
    {
        Unbind();
        _vm = DataContext as OrderFlowCubeViewModel;
        if (_vm is not null)
        {
            _vm.TrailPoints.CollectionChanged += OnTrailChanged;
            Redraw();
        }
    }

    private void Unbind()
    {
        if (_vm is not null) _vm.TrailPoints.CollectionChanged -= OnTrailChanged;
        _vm = null;
    }

    private void OnTrailChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        if (_vm is null) return;
        var plot = CubePlot.Plot;
        plot.Clear();

        var pts = _vm.TrailPoints;
        if (pts.Count > 0)
        {
            var xs = new double[pts.Count];
            var ys = new double[pts.Count];
            for (var i = 0; i < pts.Count; i++)
            {
                xs[i] = pts[i].Aggressor;
                ys[i] = pts[i].Cvd;
            }
            plot.Add.ScatterPoints(xs, ys);
        }

        plot.FigureBackground.Color = Color.FromHex("#000000");
        plot.DataBackground.Color = Color.FromHex("#0A0A0A");
        plot.Axes.Color(Color.FromHex("#8A8A8A"));
        plot.XLabel("Aggressor ratio");
        plot.YLabel("CVD");
        CubePlot.Refresh();
    }
}
