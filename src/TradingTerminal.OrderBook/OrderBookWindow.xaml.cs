using MahApps.Metro.Controls;

namespace TradingTerminal.OrderBook;

/// <summary>
/// Hosts the standalone Order Book ladder. Pure view: it just renders <see cref="OrderBookViewModel"/>
/// (instrument picker + bid/ask depth ladders). No business logic lives here (MVVM rule) — the VM owns
/// the depth subscription and projection.
/// </summary>
public partial class OrderBookWindow : MetroWindow
{
    public OrderBookWindow() => InitializeComponent();
}
