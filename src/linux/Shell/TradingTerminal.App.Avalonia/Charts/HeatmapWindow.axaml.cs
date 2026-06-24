using Avalonia.Controls;

namespace TradingTerminal.App.Avalonia.Charts;

public partial class HeatmapWindow : Window
{
    public HeatmapWindow() => InitializeComponent();

    protected override void OnClosed(System.EventArgs e)
    {
        (DataContext as HeatmapViewModel)?.Dispose();
        base.OnClosed(e);
    }
}
