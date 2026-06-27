using Avalonia.Controls;

namespace TradingTerminal.Ai.FactorResearch.AvaloniaUi;

/// <summary>Avalonia (cross-platform) view for the Factor Research tool — net9.0-leg counterpart to
/// the WPF UserControl. File dialogs go through the portable UiFile seam. Functional view binding the
/// portable VM; the full correlation-matrix heatmap is a later parity pass.</summary>
public partial class FactorResearchAvaloniaWindow : Window
{
    public FactorResearchAvaloniaWindow() => InitializeComponent();
}
