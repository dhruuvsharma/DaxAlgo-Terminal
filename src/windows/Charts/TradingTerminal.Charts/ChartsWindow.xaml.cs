using MahApps.Metro.Controls;

namespace TradingTerminal.Charts;

/// <summary>
/// The standalone Charts window: a frame around <see cref="ChartsPanel"/> with every feature on. The
/// WebView2 bridge lives in the panel (and tears itself down when this window closes), so the strategy
/// windows that embed it get exactly the chart this tool shows. The shell window host owns and disposes
/// the standalone view-model exactly once.
/// </summary>
public partial class ChartsWindow : MetroWindow
{
    public ChartsWindow() => InitializeComponent();
}
