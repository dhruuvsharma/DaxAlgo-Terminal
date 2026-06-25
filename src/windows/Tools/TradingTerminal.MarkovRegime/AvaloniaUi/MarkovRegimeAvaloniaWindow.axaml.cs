using Avalonia.Controls;

namespace TradingTerminal.MarkovRegime.AvaloniaUi;

/// <summary>Avalonia (cross-platform) view for the Markov Regime tool — net9.0-leg counterpart to the
/// WPF ScottPlot view. Shows the fitted states + transition matrix as text/lists; the price+regime
/// ribbon chart is a later parity pass. File-free, so no UiFile needed.</summary>
public partial class MarkovRegimeAvaloniaWindow : Window
{
    public MarkovRegimeAvaloniaWindow() => InitializeComponent();
}
