using Avalonia.Controls;

namespace TradingTerminal.Ai.MlFeatures.AvaloniaUi;

/// <summary>Avalonia (cross-platform) view for the ML Features tool — net9.0-leg counterpart to the
/// WPF UserControl. File dialogs go through the portable UiFile seam.</summary>
public partial class MlFeaturesAvaloniaWindow : Window
{
    public MlFeaturesAvaloniaWindow() => InitializeComponent();
}
