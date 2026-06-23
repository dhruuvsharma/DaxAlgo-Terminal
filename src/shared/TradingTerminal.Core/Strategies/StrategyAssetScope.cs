namespace TradingTerminal.Core.Strategies;

/// <summary>
/// Whether a strategy operates on a <em>single instrument at a time</em> or aggregates several
/// instruments simultaneously. Orthogonal to <see cref="ITradingStrategy.AssetClasses"/> (which
/// asset classes it's designed for): a single-asset strategy can still be asset-agnostic, and a
/// multi-asset monitor can still span just one class (e.g. an all-equity universe).
/// </summary>
public enum StrategyAssetScope
{
    /// <summary>
    /// Trades / signals on one instrument at a time (the usual case). The user points the
    /// strategy window at a single <c>SignalInstrument</c>.
    /// </summary>
    SingleAsset,

    /// <summary>
    /// Consumes / aggregates many instruments at once — index composites, cross-asset monitors,
    /// universe heatmaps (e.g. Index K-Score Surface, Order Flow Pressure Map, Index Regime Graph).
    /// </summary>
    MultiAsset,
}
