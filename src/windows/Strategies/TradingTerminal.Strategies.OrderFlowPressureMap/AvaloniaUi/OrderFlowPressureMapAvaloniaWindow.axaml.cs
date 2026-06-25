using Avalonia.Controls;

namespace TradingTerminal.Strategies.OrderFlowPressureMap.AvaloniaUi;

/// <summary>
/// Avalonia (cross-platform) view for the Order Flow Pressure Map — net9.0-leg counterpart to the WPF
/// window. Functional placeholder binding the portable VM (filters + status + pinned detail); the
/// ticker×time heatmap matrix is a later parity pass. Registered via StrategyFactoryRegistration.
/// </summary>
public partial class OrderFlowPressureMapAvaloniaWindow : Window
{
    public OrderFlowPressureMapAvaloniaWindow() => InitializeComponent();
}
