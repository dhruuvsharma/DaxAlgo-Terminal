using Avalonia.Controls;

namespace TradingTerminal.Strategies.IndexRegimeGraph.AvaloniaUi;

/// <summary>
/// Avalonia (cross-platform) view for the Index Regime Graph strategy — net9.0-leg counterpart to the
/// WPF node-graph window. Functional placeholder binding the portable VM; the faithful pan/zoom graph
/// + heatmap table is a later parity pass. Registered via StrategyFactoryRegistration.
/// </summary>
public partial class IndexRegimeGraphAvaloniaWindow : Window
{
    public IndexRegimeGraphAvaloniaWindow() => InitializeComponent();
}
