using Avalonia.Controls;

namespace TradingTerminal.Ai.BacktestAnalysis.AvaloniaUi;

/// <summary>Avalonia (cross-platform) view for the Backtest Analysis tool — net9.0-leg counterpart to
/// the WPF UserControl. File dialogs go through the portable UiFile seam.</summary>
public partial class BacktestAnalysisAvaloniaWindow : Window
{
    public BacktestAnalysisAvaloniaWindow() => InitializeComponent();
}
