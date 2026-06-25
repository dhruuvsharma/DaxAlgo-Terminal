using Avalonia.Controls;

namespace TradingTerminal.Strategies.OrnsteinUhlenbeck.AvaloniaUi;

/// <summary>
/// Avalonia (cross-platform) view for the Ornstein-Uhlenbeck strategy. Lives in the strategy
/// project — the net9.0 leg's counterpart to the WPF <c>OrnsteinUhlenbeckStrategyWindow</c> — and is
/// registered via a <c>StrategyFactoryRegistration</c> so the shell opens it through IStrategyFactory.
/// </summary>
public partial class OrnsteinUhlenbeckAvaloniaWindow : Window
{
    public OrnsteinUhlenbeckAvaloniaWindow() => InitializeComponent();
}
