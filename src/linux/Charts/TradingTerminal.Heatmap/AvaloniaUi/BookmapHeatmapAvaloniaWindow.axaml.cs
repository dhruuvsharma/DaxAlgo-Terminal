using Avalonia.Controls;

namespace TradingTerminal.Heatmap.AvaloniaUi;

/// <summary>Avalonia (cross-platform) view for the Bookmap + VolBook tool — net9.0-leg counterpart to
/// the WPF window. Exposes the instrument picker, live microstructure readouts (bid/ask/mid/VWAP/CVD/
/// volume), display toggles, and the playback scrubber from the portable BookmapHeatmapViewModel
/// (which streams depth + trades off the hub). The faithful WriteableBitmap bookmap pixel render
/// (BookmapSurface on WPF) is a later polish pass.</summary>
public partial class BookmapHeatmapAvaloniaWindow : Window
{
    public BookmapHeatmapAvaloniaWindow() => InitializeComponent();
}
