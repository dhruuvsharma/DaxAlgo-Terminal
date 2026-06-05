using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Heatmap;

/// <summary>
/// Bookmap-style depth/liquidity heatmap: each cell is the <em>raw resting size</em> at that price
/// level and moment, so both sides of the book light up as bright bands (sequential colour). See
/// <see cref="DepthColumnHeatmapViewModelBase"/> for the snapshot-column machinery.
/// </summary>
public sealed class DepthHeatmapViewModel : DepthColumnHeatmapViewModelBase
{
    public DepthHeatmapViewModel(
        IMarketDataRepository repository,
        IMarketDataHub hub,
        IMarketDataIngest ingest,
        IBrokerSelector selector,
        ILogger<DepthHeatmapViewModel> logger)
        : base(repository, hub, ingest, selector, logger)
    {
        Status = "Pick an instrument to stream its depth heatmap.";
    }

    protected override HeatmapPalette Palette => HeatmapPalette.Sequential;

    protected override double CellValue(long size, bool isBid) => size;
}
