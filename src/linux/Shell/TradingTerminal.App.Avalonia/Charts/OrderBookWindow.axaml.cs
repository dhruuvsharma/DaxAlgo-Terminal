using Avalonia.Controls;

namespace TradingTerminal.App.Avalonia.Charts;

public partial class OrderBookWindow : Window
{
    public OrderBookWindow() => InitializeComponent();

    protected override void OnClosed(System.EventArgs e)
    {
        (DataContext as OrderBookViewModel)?.Dispose();
        base.OnClosed(e);
    }
}
