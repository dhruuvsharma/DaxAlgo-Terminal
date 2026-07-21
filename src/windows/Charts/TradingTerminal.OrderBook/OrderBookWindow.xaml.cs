using MahApps.Metro.Controls;

namespace TradingTerminal.OrderBook;

/// <summary>
/// The standalone Order Book window: a frame around <see cref="OrderBookPanel"/> with every feature on.
/// All of the depth/heatmap/ML rendering lives in the panel, so the strategy windows that embed it get
/// exactly the book this tool shows. The shell window host owns the standalone view-model lifetime and
/// disposes its depth/trade subscriptions exactly once.
/// </summary>
public partial class OrderBookWindow : MetroWindow
{
    public OrderBookWindow() => InitializeComponent();
}
