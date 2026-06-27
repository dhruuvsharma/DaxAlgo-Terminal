using Avalonia.Controls;

namespace TradingTerminal.Strategies.CumulativeDelta.AvaloniaUi;

/// <summary>
/// Avalonia (cross-platform) view for the Cumulative-Delta scalper — the net9.0-leg counterpart to
/// the WPF window. Bespoke (its VM derives from ViewModelBase, not the live-signal base), registered
/// via a StrategyFactoryRegistration so the shell opens it through IStrategyFactory.
/// </summary>
public partial class CumulativeDeltaAvaloniaWindow : Window
{
    public CumulativeDeltaAvaloniaWindow() => InitializeComponent();
}
