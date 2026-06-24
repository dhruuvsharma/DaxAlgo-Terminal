using Avalonia.Controls;
using TradingTerminal.App.Avalonia.ViewModels;

namespace TradingTerminal.App.Avalonia.Views.Charts;

public partial class VolumeFootprintWindow : Window
{
    public VolumeFootprintWindow() => InitializeComponent();

    protected override void OnClosed(System.EventArgs e)
    {
        (DataContext as VolumeFootprintViewModel)?.Dispose();
        base.OnClosed(e);
    }
}
