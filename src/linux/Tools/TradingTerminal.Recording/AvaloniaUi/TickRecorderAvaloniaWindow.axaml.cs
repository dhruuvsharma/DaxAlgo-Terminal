using Avalonia.Controls;

namespace TradingTerminal.Recording.AvaloniaUi;

/// <summary>Avalonia (cross-platform) view for the live tick recorder — net9.0-leg counterpart to the
/// WPF view. Save dialog via the portable UiFile seam.</summary>
public partial class TickRecorderAvaloniaWindow : Window
{
    public TickRecorderAvaloniaWindow() => InitializeComponent();
}
