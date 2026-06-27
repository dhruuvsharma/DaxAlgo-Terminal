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

/// <summary>Knobs for the genetic optimizer. Defaults are a small, fast search suitable for the
/// interactive Studio; widen for headless runs.</summary>
public sealed record GeneticOptions(
    int PopulationSize = 24,
    int Generations = 10,
    double MutationRate = 0.2,
    int Elites = 2,
    int TournamentSize = 3,
    int Seed = 1);

/// <summary>
/// One fold of a walk-forward analysis: parameters chosen by optimizing on the in-sample window, and
/// how they performed on the immediately-following out-of-sample window. The IS→OOS score gap is the
/// honest test of whether an optimization generalizes or just overfit the training slice.
/// </summary>
public sealed record WalkForwardFold(
    DateTime InSampleFromUtc,
    DateTime InSampleToUtc,
    DateTime OutOfSampleFromUtc,
    DateTime OutOfSampleToUtc,
    IReadOnlyDictionary<string, double> BestParameters,
    double InSampleScore,
    double OutOfSampleScore,
    double OutOfSampleNetProfit,
    int OutOfSampleTradeCount);

/// <summary>The result of a walk-forward run: every fold plus headline aggregates. <see cref="Efficiency"/>
/// (avg OOS score / avg IS score) near 1 means the optimization held up out of sample; near 0 means it
/// didn't.</summary>
public sealed record WalkForwardResult(IReadOnlyList<WalkForwardFold> Folds)
{
    public double AvgInSampleScore => Folds.Count == 0 ? 0 : Folds.Average(f => f.InSampleScore);
    public double AvgOutOfSampleScore => Folds.Count == 0 ? 0 : Folds.Average(f => f.OutOfSampleScore);
    public double TotalOutOfSampleNetProfit => Folds.Sum(f => f.OutOfSampleNetProfit);
    public double Efficiency => AvgInSampleScore == 0 ? 0 : AvgOutOfSampleScore / AvgInSampleScore;
}
