using MahApps.Metro.Controls;

namespace TradingTerminal.Charts;

/// <summary>
/// The standalone Charts window: a frame around <see cref="ChartsPanel"/> with every feature on. The
/// WebView2 bridge lives in the panel (and tears itself down when this window closes), so the strategy
/// windows that embed it get exactly the chart this tool shows. The window keeps only what is genuinely a
/// window's job — owning the view-model's lifetime, and disposing it on close.
/// </summary>
public partial class ChartsWindow : MetroWindow
{
    public ChartsWindow() => InitializeComponent();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as ChartsViewModel)?.Dispose();
    }
}
