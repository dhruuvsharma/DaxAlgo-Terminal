using Avalonia.Controls;

namespace TradingTerminal.VolumeFootprint.AvaloniaUi;

/// <summary>Avalonia (cross-platform) view for the Volume Footprint — net9.0-leg counterpart to the
/// WPF window. Exposes the instrument/interval controls + the live microstructure metrics off the
/// portable VolumeFootprintViewModel (which streams the trade tape off the hub). The faithful
/// per-cell bid/ask footprint grid (custom-drawn in the WPF view) is a later polish pass.</summary>
public partial class VolumeFootprintAvaloniaWindow : Window
{
    public VolumeFootprintAvaloniaWindow() => InitializeComponent();
}
