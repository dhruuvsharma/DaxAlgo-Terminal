namespace TradingTerminal.Core.Domain;

/// <summary>
/// Aggregated L2 order book snapshot at a point in time. <see cref="Bids"/> are sorted
/// **descending** by price (best bid first); <see cref="Asks"/> are sorted **ascending**
/// (best ask first). Empty lists indicate a one-sided book (extreme illiquidity / opening
/// auction).
///
/// Snapshots are produced by an order-book reconstruction layer on top of incremental
/// broker depth events (e.g. cTrader's <c>ProtoOADepthEvent</c> add/delete quotes, or IB's
/// <c>updateMktDepth</c> row-level updates). Consumers see only consistent snapshots,
/// never partial diffs.
/// </summary>
public sealed record DepthSnapshot(
    DateTime TimestampUtc,
    IReadOnlyList<DepthLevel> Bids,
    IReadOnlyList<DepthLevel> Asks)
{
    /// <summary>Best (highest) bid, or 0 when the bid side is empty.</summary>
    public double BestBid => Bids.Count > 0 ? Bids[0].Price : 0;

    /// <summary>Best (lowest) ask, or 0 when the ask side is empty.</summary>
    public double BestAsk => Asks.Count > 0 ? Asks[0].Price : 0;

    /// <summary>Best-bid size, 0 when empty.</summary>
    public long BestBidSize => Bids.Count > 0 ? Bids[0].Size : 0;

    /// <summary>Best-ask size, 0 when empty.</summary>
    public long BestAskSize => Asks.Count > 0 ? Asks[0].Size : 0;
}
