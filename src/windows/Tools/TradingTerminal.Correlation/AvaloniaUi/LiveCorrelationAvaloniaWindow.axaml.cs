using System.ComponentModel;
using Avalonia.Controls;
using ScottPlot;

namespace TradingTerminal.Correlation.AvaloniaUi;

/// <summary>Avalonia (cross-platform) view for the Live Correlation Matrix — net9.0-leg counterpart to
/// the WPF window. Instrument checklist + live sampler controls; the NxN matrix renders as a
/// ScottPlot.Avalonia heatmap on each MatrixResult update. Leak-safe: subscribes while attached,
/// detaches on close (the VM is disposed by the shell).</summary>
public partial class LiveCorrelationAvaloniaWindow : Window
{
    private LiveCorrelationMatrixViewModel? _vm;

    public LiveCorrelationAvaloniaWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rebind();
        DetachedFromVisualTree += (_, _) => Unbind();
    }

    private void Rebind()
    {
        Unbind();
        _vm = DataContext as LiveCorrelationMatrixViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            Redraw();
        }
    }

    private void Unbind()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "MatrixResult") Redraw();
    }

    private void Redraw()
    {
        var plot = MatrixPlot.Plot;
        plot.Clear();

        if (_vm?.MatrixResult is { } m && m.Size > 0)
            plot.Add.Heatmap(m.Values);

        plot.FigureBackground.Color = Color.FromHex("#000000");
        plot.DataBackground.Color = Color.FromHex("#0A0A0A");
        plot.Axes.Color(Color.FromHex("#8A8A8A"));
        MatrixPlot.Refresh();
    }
}
