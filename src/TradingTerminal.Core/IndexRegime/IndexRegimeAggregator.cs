using TradingTerminal.Core.IndexKScore;
using TradingTerminal.Core.MarketData.AdvancedRegime;

namespace TradingTerminal.Core.IndexRegime;

/// <summary>
/// Pure function — no I/O, no clocks beyond the supplied timestamp. Turns each constituent's
/// eight-timeframe <see cref="AdvancedRegimeSnapshot"/> into a single horizon-blended score, weights
/// it by the constituent's index weight, and sums to the composite index direction.
///
/// <para>Mirrors how the per-instrument / advanced regime calculators handle missing inputs: a
/// constituent whose snapshot is unavailable drops out cleanly (its index weight is redistributed
/// across the names that do have data) rather than dragging the composite toward zero.</para>
/// </summary>
public static class IndexRegimeAggregator
{
    // Composite/stock band thresholds on the [-1, +1] score.
    private const double StrongThreshold = 0.50;
    private const double DirectionalThreshold = 0.15;

    public static IndexRegimeSnapshot Aggregate(
        string familyName,
        RegimeHorizon horizon,
        IReadOnlyList<(IndexComponent Component, AdvancedRegimeSnapshot Snapshot)> inputs,
        DateTime nowUtc)
    {
        var weights = TimeframeWeighting.For(horizon);

        // ── Pass 1: per-constituent stock score (no index weighting yet). ───────────────────
        var staged = new List<(IndexComponent Component, double StockScore,
            IReadOnlyList<TimeframeScore> Tfs, IReadOnlyList<AdvancedRegimeColumn> Columns, bool HasData)>(inputs.Count);

        double validWeightSum = 0;
        foreach (var (component, snapshot) in inputs)
        {
            var columns = snapshot?.Columns ?? Array.Empty<AdvancedRegimeColumn>();
            var hasData = snapshot is { Unavailable: false } && columns.Count > 0;

            var tfs = new List<TimeframeScore>(columns.Count);
            double weighted = 0, wSum = 0;
            foreach (var col in columns)
            {
                var score = Math.Clamp(col.TrendScore / 8.0, -1.0, 1.0);
                tfs.Add(new TimeframeScore(col.Timeframe.Label, score, col.TrendScore));
                var w = weights.TryGetValue(col.Timeframe.Label, out var wv) ? wv : TimeframeWeighting.FloorWeight;
                weighted += score * w;
                wSum += w;
            }

            var stockScore = wSum > 1e-9 ? weighted / wSum : 0.0;
            if (!hasData) stockScore = 0.0;
            if (hasData) validWeightSum += component.IndexWeight;

            staged.Add((component, stockScore, tfs, columns, hasData));
        }

        // ── Pass 2: renormalise index weights over the names with data, build the composite. ─
        var rows = new List<ConstituentRegimeScore>(staged.Count);
        double composite = 0;
        int bullish = 0, bearish = 0, withData = 0;

        foreach (var (component, stockScore, tfs, columns, hasData) in staged)
        {
            var normalizedWeight = hasData && validWeightSum > 1e-9
                ? component.IndexWeight / validWeightSum
                : 0.0;
            var contribution = stockScore * normalizedWeight;
            composite += contribution;

            if (hasData)
            {
                withData++;
                if (stockScore > 0) bullish++;
                else if (stockScore < 0) bearish++;
            }

            rows.Add(new ConstituentRegimeScore(
                Symbol: component.Symbol,
                DisplayName: component.DisplayName,
                IndexWeight: component.IndexWeight,
                NormalizedWeight: normalizedWeight,
                StockScore: stockScore,
                Contribution: contribution,
                Band: BandFor(stockScore),
                TimeframeScores: tfs,
                Columns: columns,
                HasData: hasData));
        }

        composite = Math.Clamp(composite, -1.0, 1.0);

        return new IndexRegimeSnapshot(
            TimestampUtc: nowUtc,
            FamilyName: familyName,
            Horizon: horizon,
            Constituents: rows,
            CompositeScore: composite,
            Band: BandFor(composite),
            BullishCount: bullish,
            BearishCount: bearish,
            ConstituentsWithData: withData,
            ConstituentsTotal: staged.Count);
    }

    /// <summary>Maps a signed score in <c>[-1, +1]</c> to the five-level <see cref="CellSignal"/>
    /// band (reused for both per-stock and composite bands).</summary>
    public static CellSignal BandFor(double score) => score switch
    {
        >= StrongThreshold       => CellSignal.StrongUp,
        >= DirectionalThreshold  => CellSignal.Up,
        <= -StrongThreshold      => CellSignal.StrongDown,
        <= -DirectionalThreshold => CellSignal.Down,
        _                        => CellSignal.Neutral,
    };
}
