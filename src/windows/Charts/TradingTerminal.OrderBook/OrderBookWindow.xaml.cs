using System.Windows;
using MahApps.Metro.Controls;

namespace TradingTerminal.OrderBook;

/// <summary>
/// The standalone Order Book window: a frame around <see cref="OrderBookPanel"/> with every feature on.
/// All of the depth/heatmap/ML rendering lives in the panel, so the strategy windows that embed it get
/// exactly the book this tool shows. The window keeps only what is genuinely a window's job — owning the
/// view-model's lifetime, and disposing it (which drops the depth/trade subscriptions) on close.
/// </summary>
public partial class OrderBookWindow : MetroWindow
{
    public OrderBookWindow() => InitializeComponent();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as OrderBookViewModel)?.Dispose();
    }
}
