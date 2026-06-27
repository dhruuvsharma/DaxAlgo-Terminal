using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.IndexKScore;

/// <summary>
/// Index component descriptor. <see cref="IndexWeight"/> is the index-level weight (0..1);
/// the aggregator scales the per-stock piercing threshold inversely with this weight.
/// </summary>
public sealed record IndexComponent(
    string Symbol,
    string DisplayName,
    double IndexWeight,
    Contract Contract,
    BrokerKind? Broker = null);

/// <summary>
/// Holds the per-component K-state for an index and computes the threshold surface plus the
/// index-level entry signal whenever a component snapshot lands. The aggregator is dumb data
/// + math; the host VM owns timing, broker subscriptions, and event publication.
/// </summary>
public sealed class IndexKScoreAggregator
{
    private readonly Dictionary<string, ComponentState> _components;
    private readonly double _weightMin;
    private readonly double _weightMax;

    public double TMin { get; }
    public double TMax { get; }
    public int MinPierceCount { get; }
    public double CumKThreshold { get; }

    public IndexKScoreAggregator(
        IReadOnlyList<IndexComponent> components,
        double tMin,
        double tMax,
        int minPierceCount,
        double cumKThreshold)
    {
        if (components.Count == 0) throw new ArgumentException("At least one component is required.", nameof(components));
        if (tMin <= 0 || tMax <= 0 || tMin >= tMax)
            throw new ArgumentException("Thresholds require 0 < T_min < T_max.");
        if (minPierceCount < 1) throw new ArgumentException("MinPierceCount must be >= 1.");
        if (cumKThreshold <= 0) throw new ArgumentException("CumKThreshold must be > 0.");

        TMin = tMin;
        TMax = tMax;
        MinPierceCount = minPierceCount;
        CumKThreshold = cumKThreshold;

        _weightMin = components.Min(c => c.IndexWeight);
        _weightMax = components.Max(c => c.IndexWeight);
        if (_weightMax - _weightMin < 1e-12)
            throw new ArgumentException("Component weights are degenerate (max ≈ min).");

        _components = components.ToDictionary(
            c => c.Symbol,
            c => new ComponentState
            {
                Component = c,
                Threshold = ComputeThreshold(c.IndexWeight),
            });
    }

    public double ComputeThreshold(double weight)
    {
        var norm = (weight - _weightMin) / (_weightMax - _weightMin);
        return TMax - (TMax - TMin) * norm;
    }

    /// <summary>Updates the component snapshot and returns the new index-level aggregate. Returns
    /// null if the symbol isn't tracked.</summary>
    public IndexSnapshot? Update(string symbol, IndexKScoreCalculator.Snapshot snapshot)
    {
        if (!_components.TryGetValue(symbol, out var state)) return null;
        state.Latest = snapshot;
        state.HasOutput = true;
        return BuildAggregate(DateTime.UtcNow);
    }

    public IndexSnapshot BuildAggregate(DateTime asOfUtc)
    {
        var rows = new List<ComponentSnapshot>(_components.Count);
        var piercingUp = 0; var piercingDown = 0;
        var cumKUp = 0.0; var cumKDown = 0.0;

        foreach (var (_, state) in _components)
        {
            var c = state.Component;
            if (!state.HasOutput)
            {
                rows.Add(new ComponentSnapshot(
                    c.Symbol, c.DisplayName, c.IndexWeight,
                    KRaw: 0, KFinal: 0, Confidence: 1, Threshold: state.Threshold,
                    IsPiercing: false, PierceDirection: 0,
                    Breakdown: default,
                    HasData: false,
                    Overbought: false, Oversold: false));
                continue;
            }

            var snap = state.Latest;
            var pierceUp = snap.KFinal > state.Threshold;
            var pierceDown = snap.KFinal < -state.Threshold;
            var dir = pierceUp ? +1 : pierceDown ? -1 : 0;
            if (pierceUp) { piercingUp++; cumKUp += snap.KFinal; }
            if (pierceDown) { piercingDown++; cumKDown += Math.Abs(snap.KFinal); }

            rows.Add(new ComponentSnapshot(
                c.Symbol, c.DisplayName, c.IndexWeight,
                snap.KRaw, snap.KFinal, snap.Confidence, state.Threshold,
                IsPiercing: dir != 0,
                PierceDirection: dir,
                Breakdown: snap.Breakdown,
                HasData: true,
                Overbought: snap.Overbought, Oversold: snap.Oversold));
        }

        var longActive = piercingUp >= MinPierceCount && cumKUp >= CumKThreshold;
        var shortActive = piercingDown >= MinPierceCount && cumKDown >= CumKThreshold;

        return new IndexSnapshot(
            asOfUtc,
            rows,
            piercingUp, piercingDown,
            cumKUp, cumKDown,
            longActive, shortActive);
    }

    public void Reset()
    {
        foreach (var (_, state) in _components)
        {
            state.HasOutput = false;
            state.Latest = default;
        }
    }

    public IReadOnlyDictionary<string, double> ThresholdsBySymbol =>
        _components.ToDictionary(kv => kv.Key, kv => kv.Value.Threshold);

    private sealed class ComponentState
    {
        public IndexComponent Component { get; init; } = null!;
        public double Threshold { get; init; }
        public bool HasOutput { get; set; }
        public IndexKScoreCalculator.Snapshot Latest { get; set; }
    }
}

public sealed record ComponentSnapshot(
    string Symbol,
    string DisplayName,
    double IndexWeight,
    double KRaw,
    double KFinal,
    double Confidence,
    double Threshold,
    bool IsPiercing,
    int PierceDirection,
    IndexKScoreCalculator.SignalBreakdown Breakdown,
    bool HasData,
    bool Overbought,
    bool Oversold);

public sealed record IndexSnapshot(
    DateTime TimestampUtc,
    IReadOnlyList<ComponentSnapshot> Components,
    int PiercingUpCount,
    int PiercingDownCount,
    double CumulativeKUp,
    double CumulativeKDown,
    bool LongSignalActive,
    bool ShortSignalActive);
