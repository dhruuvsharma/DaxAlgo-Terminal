using Avalonia.Controls;

namespace TradingTerminal.OrderBook.AvaloniaUi;

/// <summary>Avalonia (cross-platform) view for the Order Book — net9.0-leg counterpart to the WPF
/// window. Renders the live bid/ask depth ladder (with per-level depth bars) + microstructure metrics
/// from the portable OrderBookViewModel (which streams off the canonical hub).</summary>
public partial class OrderBookAvaloniaWindow : Window
{
    public OrderBookAvaloniaWindow() => InitializeComponent();
}
