using Avalonia.Controls;

namespace TradingTerminal.Strategies.SigmaIcFlow.AvaloniaUi;

/// <summary>
/// Avalonia (cross-platform) view for the Σ⁻¹·IC Order-Flow Optimizer — the net9.0-leg counterpart
/// to the WPF MetroWindow. Bound to the shared <see cref="SigmaIcFlowStrategyViewModel"/> (which
/// derives from the live-signal base), registered via a StrategyFactoryRegistration so the Avalonia
/// shell opens it through IStrategyFactory. The footprint/ScottPlot/HelixToolkit visuals from the WPF
/// window are intentionally dropped here — this surfaces the configure flow plus the optimizer's
/// numeric dashboard (composite, regime, weight vector, signal states, paper blotter).
/// </summary>
public partial class SigmaIcFlowStrategyAvaloniaWindow : Window
{
    public SigmaIcFlowStrategyAvaloniaWindow() => InitializeComponent();
}
