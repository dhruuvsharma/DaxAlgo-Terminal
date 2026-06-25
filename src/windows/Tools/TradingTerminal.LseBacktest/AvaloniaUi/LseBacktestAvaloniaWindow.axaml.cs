using Avalonia.Controls;

namespace TradingTerminal.LseBacktest.AvaloniaUi;

/// <summary>Avalonia (cross-platform) view for the LSE backtester — net9.0-leg counterpart to the WPF
/// ScottPlot view. Functional view: strategy/symbol/timeframe setup + Run + P&amp;L + trades. The
/// equity-curve ScottPlot chart is a later parity pass.</summary>
public partial class LseBacktestAvaloniaWindow : Window
{
    public LseBacktestAvaloniaWindow() => InitializeComponent();
}
