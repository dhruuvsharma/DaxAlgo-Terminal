using TradingTerminal.Core.MarketData.AdvancedRegime;

namespace TradingTerminal.Core.IndexRegime;

/// <summary>One constituent timeframe column collapsed to a signed score in <c>[-1, +1]</c>
/// (<c>TrendScore / 8</c>). <see cref="TrendScore"/> is the raw Advanced-Regime needle (-8..+8),
/// kept for the graph's timeframe-node labels.</summary>
public readonly record struct TimeframeScore(string Label, double Score, int TrendScore);

/// <summary>
/// One index constituent after its eight-timeframe Advanced-Regime analysis has been blended for a
/// horizon. <see cref="StockScore"/> ∈ [-1, +1] is the horizon-weighted mean of the per-timeframe
/// scores; <see cref="Contribution"/> = <see cref="StockScore"/> × <see cref="NormalizedWeight"/> is
/// what this name adds to the index composite. <see cref="Columns"/> carries the raw Advanced-Regime
/// columns so the node graph can expand a stock into its timeframe and indicator-cell nodes.
/// </summary>
public sealed record ConstituentRegimeScore(
    string Symbol,
    string DisplayName,
    double IndexWeight,
    double NormalizedWeight,
    double StockScore,
    double Contribution,
    CellSignal Band,
    IReadOnlyList<TimeframeScore> TimeframeScores,
    IReadOnlyList<AdvancedRegimeColumn> Columns,
    bool HasData);

/// <summary>
/// A full index-direction snapshot: the per-constituent breakdown plus the weighted composite.
/// <see cref="CompositeScore"/> ∈ [-1, +1] = Σ (stockScore × normalizedWeight) over constituents
/// with data (weights renormalised over those names). Immutable — a refresh swaps a whole new
/// snapshot in.
/// </summary>
public sealed record IndexRegimeSnapshot(
    DateTime TimestampUtc,
    string FamilyName,
    RegimeHorizon Horizon,
    IReadOnlyList<ConstituentRegimeScore> Constituents,
    double CompositeScore,
    CellSignal Band,
    int BullishCount,
    int BearishCount,
    int ConstituentsWithData,
    int ConstituentsTotal)
{
    public static IndexRegimeSnapshot Empty { get; } = new(
        TimestampUtc: DateTime.MinValue,
        FamilyName: string.Empty,
        Horizon: RegimeHorizon.Intraday,
        Constituents: Array.Empty<ConstituentRegimeScore>(),
        CompositeScore: 0,
        Band: CellSignal.Neutral,
        BullishCount: 0,
        BearishCount: 0,
        ConstituentsWithData: 0,
        ConstituentsTotal: 0);
}
