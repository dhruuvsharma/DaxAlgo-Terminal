using System.ComponentModel;
using Avalonia.Controls;
using ScottPlot;

namespace TradingTerminal.QuantConnect.AvaloniaUi;

/// <summary>Avalonia (cross-platform) view for the QuantConnect / LEAN tool — net9.0-leg counterpart to
/// the WPF 4-tab window. The equity curve renders as a ScottPlot.Avalonia line from the portable
/// EquityValues array. Leak-safe: subscribes while attached, detaches on close.</summary>
public partial class QuantConnectAvaloniaWindow : Window
{
    private QuantConnectViewModel? _vm;

    public QuantConnectAvaloniaWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rebind();
        DetachedFromVisualTree += (_, _) => Unbind();
    }

    private void Rebind()
    {
        Unbind();
        _vm = DataContext as QuantConnectViewModel;
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
        if (e.PropertyName == "EquityValues") Redraw();
    }

    private void Redraw()
    {
        var plot = EquityPlot.Plot;
        plot.Clear();

        if (_vm?.EquityValues is { Length: >= 2 } values)
            plot.Add.Signal(values);

        plot.FigureBackground.Color = Color.FromHex("#000000");
        plot.DataBackground.Color = Color.FromHex("#0A0A0A");
        plot.Axes.Color(Color.FromHex("#8A8A8A"));
        EquityPlot.Refresh();
    }
}
