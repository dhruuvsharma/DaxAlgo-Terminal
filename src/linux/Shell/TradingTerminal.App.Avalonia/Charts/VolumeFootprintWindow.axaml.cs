using Avalonia.Controls;

namespace TradingTerminal.App.Avalonia.Charts;

public partial class VolumeFootprintWindow : Window
{
    public VolumeFootprintWindow() => InitializeComponent();

    protected override void OnClosed(System.EventArgs e)
    {
        (DataContext as VolumeFootprintViewModel)?.Dispose();
        base.OnClosed(e);
    }
}
