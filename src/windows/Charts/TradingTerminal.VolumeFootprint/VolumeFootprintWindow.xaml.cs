using System.Windows;
using MahApps.Metro.Controls;

namespace TradingTerminal.VolumeFootprint;

/// <summary>
/// The standalone Volume Footprint window: a frame around <see cref="VolumeFootprintPanel"/> with every
/// feature on. All of the cluster-grid rendering lives in the panel, so the strategy windows that embed
/// it get exactly the footprint this tool shows. The window keeps only what is genuinely a window's job —
/// owning the view-model's lifetime, and disposing it (which drops the trade subscription) on close.
/// </summary>
public partial class VolumeFootprintWindow : MetroWindow
{
    public VolumeFootprintWindow() => InitializeComponent();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as VolumeFootprintViewModel)?.Dispose();
    }
}
