using MahApps.Metro.Controls;

namespace TradingTerminal.VolumeFootprint;

/// <summary>
/// The standalone Volume Footprint window: a frame around <see cref="VolumeFootprintPanel"/> with every
/// feature on. All of the cluster-grid rendering lives in the panel, so the strategy windows that embed
/// it get exactly the footprint this tool shows. The shell window host owns the standalone view-model
/// lifetime and disposes its trade subscriptions exactly once.
/// </summary>
public partial class VolumeFootprintWindow : MetroWindow
{
    public VolumeFootprintWindow() => InitializeComponent();
}
