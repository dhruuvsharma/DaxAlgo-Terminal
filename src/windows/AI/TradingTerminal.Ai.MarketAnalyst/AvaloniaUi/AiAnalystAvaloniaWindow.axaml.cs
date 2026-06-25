using Avalonia.Controls;

namespace TradingTerminal.Ai.MarketAnalyst.AvaloniaUi;

/// <summary>Avalonia (cross-platform) view for the AI Market Analyst — net9.0-leg counterpart to the
/// WPF dock pane. Binds the portable VM (symbol/timeframe + Analyze + the latest AnalystReport verdict
/// + history). The vision chart images (Base64 PNGs) are a later parity pass.</summary>
public partial class AiAnalystAvaloniaWindow : Window
{
    public AiAnalystAvaloniaWindow() => InitializeComponent();
}
