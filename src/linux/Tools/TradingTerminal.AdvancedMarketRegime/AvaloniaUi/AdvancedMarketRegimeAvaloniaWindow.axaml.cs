using Avalonia.Controls;

namespace TradingTerminal.AdvancedMarketRegime.AvaloniaUi;

/// <summary>Avalonia (cross-platform) view for the Advanced Market Regime dashboard — net9.0-leg
/// counterpart to the WPF UserControl. Functional view binding the portable VM (instrument + Analyze
/// + the indicator×timeframe rows); the full coloured dashboard grid is a later parity pass.</summary>
public partial class AdvancedMarketRegimeAvaloniaWindow : Window
{
    public AdvancedMarketRegimeAvaloniaWindow() => InitializeComponent();
}
