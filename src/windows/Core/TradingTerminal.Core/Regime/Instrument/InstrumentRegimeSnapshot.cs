using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Regime.Instrument;

/// <summary>
/// One per-instrument regime computation: the signed composite, its band, the breakdown of
/// each scored sub-signal, and a few headline scalars (volatility, ATR, last close) shown
/// alongside the gauge. Immutable — refresh swaps a whole new snapshot in.
/// </summary>
/// <param name="Symbol">Display label of the analysed instrument.</param>
/// <param name="Timeframe">Bar size the analysis was run on.</param>
/// <param name="CompositeScore">Signed composite in <c>[-100, +100]</c> (positive bullish).</param>
/// <param name="Band">Mapped band from <see cref="CompositeScore"/>.</param>
/// <param name="Signals">Per-signal breakdown including the L2 signals (marked Valid=false
/// when depth wasn't available).</param>
/// <param name="LastClose">Most recent bar's close, for the header.</param>
/// <param name="AtrPercent">14-period ATR expressed as a percentage of <see cref="LastClose"/>.</param>
/// <param name="VolatilityRank">Where the current ATR sits in its 50-bar percentile
/// distribution, in <c>[0, 100]</c>. 0 = quietest in the lookback, 100 = stretched.</param>
/// <param name="BarCount">Number of bars consumed.</param>
/// <param name="DepthLevels">Total L2 levels (asks + bids) seen, or 0 when depth unavailable.</param>
/// <param name="GeneratedAtUtc">When this snapshot was computed.</param>
/// <param name="Unavailable">True for the sentinel <see cref="Empty"/> state.</param>
public sealed record InstrumentRegimeSnapshot(
    string Symbol,
    BarSize Timeframe,
    double CompositeScore,
    InstrumentRegimeBand Band,
    IReadOnlyList<InstrumentSignalScore> Signals,
    double? LastClose,
    double? AtrPercent,
    double? VolatilityRank,
    int BarCount,
    int DepthLevels,
    DateTime GeneratedAtUtc,
    bool Unavailable)
{
    public string Label => Band.Label();

    public static InstrumentRegimeSnapshot Empty { get; } = new(
        Symbol: string.Empty,
        Timeframe: BarSize.OneMinute,
        CompositeScore: 0,
        Band: InstrumentRegimeBand.Neutral,
        Signals: Array.Empty<InstrumentSignalScore>(),
        LastClose: null,
        AtrPercent: null,
        VolatilityRank: null,
        BarCount: 0,
        DepthLevels: 0,
        GeneratedAtUtc: DateTime.MinValue,
        Unavailable: true);
}
