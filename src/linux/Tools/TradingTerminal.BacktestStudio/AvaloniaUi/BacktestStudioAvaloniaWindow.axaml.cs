using Avalonia.Controls;

namespace TradingTerminal.BacktestStudio.AvaloniaUi;

/// <summary>Avalonia (cross-platform) view for Backtest Studio — net9.0-leg counterpart to the WPF
/// ScottPlot studio. Functional view: strategy/data-source/param setup + Run + report stats + trades.
/// The ScottPlot visual-replay transport bar is a later parity pass (the VM's replay timer is already
/// portable via UiThread).</summary>
public partial class BacktestStudioAvaloniaWindow : Window
{
    public BacktestStudioAvaloniaWindow() => InitializeComponent();
}
