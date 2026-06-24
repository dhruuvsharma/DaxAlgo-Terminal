namespace TradingTerminal.Core.Regime.Markov;

/// <summary>Human-readable label assigned to a latent HMM state by ranking its mean log-return:
/// the lowest-mean state is <see cref="Bearish"/>, the highest <see cref="Bullish"/>, anything in
/// between <see cref="Neutral"/>. Labels are descriptive only — the model itself is agnostic.</summary>
public enum RegimeLabel
{
    Bearish,
    Neutral,
    Bullish,
}

/// <summary>Summary of one fitted latent state.</summary>
public sealed record MarkovRegimeState(
    int Index,
    RegimeLabel Label,
    double MeanLogReturn,
    double Variance,
    double StationaryProbability,    // long-run share of time, from the transition matrix
    double OccupancyProbability,     // empirical share over this sample (mean posterior)
    double ExpectedDurationBars);    // 1 / (1 − selfTransition)

/// <summary>One observation step (one bar, from the second bar onward — the first has no return).</summary>
public sealed record MarkovRegimePoint(
    DateTime TimeUtc,
    double Close,
    double LogReturn,
    int State,
    RegimeLabel Label,
    double[] Posterior);             // P(state | whole series), length StateCount

/// <summary>Full result of fitting a Gaussian HMM to an instrument's bar series.</summary>
public sealed record MarkovRegimeResult(
    int StateCount,
    IReadOnlyList<MarkovRegimeState> States,
    double[][] TransitionMatrix,     // row-stochastic A[i][j]
    double[] InitialProbabilities,
    IReadOnlyList<MarkovRegimePoint> Series,
    double LogLikelihood,
    int Iterations,
    bool Converged)
{
    /// <summary>The state of the most recent bar (the current regime), or null if empty.</summary>
    public MarkovRegimePoint? Current => Series.Count > 0 ? Series[^1] : null;
}
