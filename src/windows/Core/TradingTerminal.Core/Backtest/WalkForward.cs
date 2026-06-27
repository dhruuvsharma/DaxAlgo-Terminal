using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Backtest;

/// <summary>
/// One candidate configuration in a walk-forward parameter grid: a label plus a factory that builds
/// a FRESH strategy instance per window. Strategies are stateful (rolling indicators, position
/// trackers), so every window must get a clean instance — hence a builder, not an instance.
/// </summary>
public sealed record WalkForwardCandidate(string Label, Func<Contract, IBacktestStrategy> Build);

/// <summary>
/// The union of parameter axes a walk-forward grid may sweep. A strategy's grid factory reads only
/// the axes it cares about and substitutes its own defaults for any the caller left empty (see
/// <see cref="Or{T}"/>), so one bag serves every strategy regardless of which knobs it exposes.
/// </summary>
public sealed record WalkForwardAxes(
    int[] Lookbacks,
    double[] Entries,
    double[] Stops,
    double[] Trails,
    double[] Thresholds,
    int[] Holds,
    double[] EntryZ,
    int Quantity)
{
    /// <summary>All-empty axes with quantity 1 — every strategy grid falls back to its own defaults.</summary>
    public static WalkForwardAxes Defaults { get; } = new([], [], [], [], [], [], [], 1);

    /// <summary>Returns <paramref name="axis"/> when the caller supplied values, else
    /// <paramref name="fallback"/> — the idiom a grid factory uses to apply its own defaults for axes
    /// left unspecified.</summary>
    public static T[] Or<T>(T[] axis, params T[] fallback) => axis is { Length: > 0 } ? axis : fallback;
}
