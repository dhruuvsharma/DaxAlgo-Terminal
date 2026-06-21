namespace TradingTerminal.Core.Backtesting;

/// <summary>
/// One axis of a parameter sweep: the values to try for a single parameter. Build from an explicit
/// set (<see cref="Of"/>) or a numeric range (<see cref="Range"/>, e.g. from a
/// <see cref="ParameterDescriptor"/>'s min/max/step). The optimizer takes the Cartesian product of
/// all axes.
/// </summary>
public sealed record ParameterAxis(string Name, IReadOnlyList<double> Values)
{
    public static ParameterAxis Of(string name, params double[] values) => new(name, values);

    public static ParameterAxis Range(string name, double min, double max, double step)
    {
        if (step <= 0) throw new ArgumentOutOfRangeException(nameof(step));
        var values = new List<double>();
        for (var v = min; v <= max + step * 1e-9; v += step) values.Add(v);
        return new ParameterAxis(name, values);
    }
}

/// <summary>What the optimizer ranks trials by. Every criterion is scored so that higher is better
/// (drawdown is negated), so the optimizer always maximizes the score.</summary>
public enum OptimizationCriterion
{
    NetProfit,
    Sharpe,
    Sortino,
    ProfitFactor,
    Calmar,
    Expectancy,
    WinRate,
    MinDrawdown,
}

/// <summary>How the parameter space is searched.</summary>
public enum OptimizationMethod
{
    /// <summary>Evaluate every point in the Cartesian product of the axes (slow-complete).</summary>
    Exhaustive,

    /// <summary>Genetic search over the space (added in a later slice).</summary>
    Genetic,
}

/// <summary>
/// A complete, serializable optimization job: the base run to vary, the axes to sweep, the criterion
/// to rank by, and the search method. The base run's non-swept parameters are kept; each trial
/// overlays its axis values onto them. Visual recording is forced off per trial regardless of the
/// base run, so a sweep never pays the timeline cost.
/// </summary>
public sealed record OptimizationSpec(
    RunSpec BaseRun,
    IReadOnlyList<ParameterAxis> Axes,
    OptimizationCriterion Criterion = OptimizationCriterion.Sharpe,
    OptimizationMethod Method = OptimizationMethod.Exhaustive,
    int MaxDegreeOfParallelism = 0);

/// <summary>One evaluated point: the parameter combination and its outcome. Headline numbers only —
/// full reports aren't retained across thousands of trials.</summary>
public sealed record OptimizationTrial(
    IReadOnlyDictionary<string, double> Parameters,
    double Score,
    double NetProfit,
    int TradeCount);

/// <summary>The outcome of a sweep: every trial (ranked best-first) and the winner.</summary>
public sealed record OptimizationResult(
    OptimizationCriterion Criterion,
    IReadOnlyList<OptimizationTrial> Trials,
    OptimizationTrial? Best)
{
    public int Evaluations => Trials.Count;
}
