namespace TradingTerminal.Core.Regime;

/// <summary>
/// The result of one composite computation: the blended 0–100 score, its band, the ten
/// scored categories, and a handful of headline metrics for the panel header. Immutable —
/// the service swaps a whole new snapshot in on each refresh.
/// </summary>
public sealed record MarketRegimeSnapshot(
    double CompositeScore,
    RegimeState State,
    double? PreviousScore,
    IReadOnlyList<RegimeCategoryScore> Categories,
    RegimeHeaderMetrics Header,
    DateTime GeneratedAtUtc,
    bool Unavailable)
{
    public string Label => State.Label();

    /// <summary>Sentinel for "we have no data yet" — shown before the first successful refresh.</summary>
    public static MarketRegimeSnapshot Empty { get; } = new(
        CompositeScore: 0,
        State: RegimeState.Neutral,
        PreviousScore: null,
        Categories: Array.Empty<RegimeCategoryScore>(),
        Header: RegimeHeaderMetrics.Empty,
        GeneratedAtUtc: DateTime.MinValue,
        Unavailable: true);
}

/// <summary>
/// Headline scalars surfaced above the category breakdown. All nullable — any missing source
/// simply leaves its metric blank in the UI. <see cref="FinancialStressIndex"/> is the
/// HYG/TLT ÷ (VIX × HY-spread) composite from the upstream project (higher = lower stress).
/// </summary>
public sealed record RegimeHeaderMetrics(
    double? Vix,
    double? PutCallRatio,
    double? HighYieldSpread,
    double? PctAbove200dma,
    double? Yield10y,
    double? FedFundsRate,
    int? CnnFearGreed,
    double? FinancialStressIndex,
    string? FinancialStressLabel)
{
    public static RegimeHeaderMetrics Empty { get; } =
        new(null, null, null, null, null, null, null, null, null);
}
