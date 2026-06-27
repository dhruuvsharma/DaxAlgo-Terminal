using Avalonia.Controls;

namespace TradingTerminal.Ai.PaperLab.AvaloniaUi;

/// <summary>Avalonia (cross-platform) view for Paper Lab — net9.0-leg counterpart to the WPF
/// UserControl. Binds the portable VM (resolve a paper → pick a repo → reproduce → jobs + confidence).
/// Job updates marshal through the head's Avalonia IUiDispatcher.</summary>
public partial class PaperLabAvaloniaWindow : Window
{
    public PaperLabAvaloniaWindow() => InitializeComponent();
}
