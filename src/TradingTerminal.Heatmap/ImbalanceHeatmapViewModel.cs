using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Heatmap;

/// <summary>
/// Order-book imbalance heatmap: same scrolling L2 history as the depth heatmap, but each cell is
/// <em>signed</em> — <c>+size</c> on the bid side, <c>-size</c> on the ask side — and drawn with a
/// diverging palette centred on zero (bid-heavy → red, ask-heavy → blue). It surfaces where support
/// (resting bids) and resistance (resting asks) stack up rather than raw magnitude.
/// </summary>
public sealed class ImbalanceHeatmapViewModel : DepthColumnHeatmapViewModelBase
{
    public ImbalanceHeatmapViewModel(
        IMarketDataRepository repository,
        IMarketDataHub hub,
        IMarketDataIngest ingest,
        IBrokerSelector selector,
        ILogger<ImbalanceHeatmapViewModel> logger)
        : base(repository, hub, ingest, selector, logger)
    {
        Status = "Pick an instrument to stream its order-book imbalance.";
    }

    protected override HeatmapPalette Palette => HeatmapPalette.Diverging;

    protected override double CellValue(long size, bool isBid) => isBid ? size : -size;
}
