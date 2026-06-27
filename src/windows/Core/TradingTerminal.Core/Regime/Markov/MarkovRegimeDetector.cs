using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Regime.Markov;

/// <summary>
/// Orchestrates Markov regime detection for an instrument: turns a bar series into per-bar log
/// returns, fits a <see cref="GaussianHmm"/>, decodes the regime path + smoothed posteriors, and
/// labels the latent states by mean return (lowest = bearish, highest = bullish). Pure and
/// deterministic for a given seed — all I/O (pulling bars from the store, charting) lives in the
/// App tool that calls this.
/// </summary>
public static class MarkovRegimeDetector
{
    /// <summary>Minimum bars required to fit <paramref name="states"/> states (returns lose one
    /// bar; EM needs a few per state to be meaningful).</summary>
    public static int MinBars(int states) => states * 10 + 2;

    public static MarkovRegimeResult Detect(IReadOnlyList<Bar> bars, int states, int seed = 12345)
    {
        if (states < 2) throw new ArgumentOutOfRangeException(nameof(states), "Use at least 2 states.");
        if (bars.Count < MinBars(states))
            throw new ArgumentException(
                $"Need at least {MinBars(states)} bars to fit {states} states; got {bars.Count}.", nameof(bars));

        // Log returns, aligned to bars[1..]. Guard non-positive closes (bad/halted data).
        var returns = new double[bars.Count - 1];
        for (var i = 1; i < bars.Count; i++)
        {
            var prev = bars[i - 1].Close;
            var cur = bars[i].Close;
            returns[i - 1] = prev > 0 && cur > 0 ? Math.Log(cur / prev) : 0.0;
        }

        var hmm = GaussianHmm.Fit(returns, states, seed: seed);
        var path = hmm.Decode(returns);
        var posteriors = hmm.PosteriorProbabilities(returns);
        var stationary = hmm.StationaryDistribution();

        // Label states by mean rank: lowest mean → Bearish, highest → Bullish, rest Neutral.
        var labels = LabelByMean(hmm.Means);

        // Empirical occupancy = mean posterior per state over the sample.
        var occupancy = new double[states];
        foreach (var row in posteriors)
            for (var s = 0; s < states; s++) occupancy[s] += row[s];
        for (var s = 0; s < states; s++) occupancy[s] /= Math.Max(posteriors.Length, 1);

        var stateSummaries = new List<MarkovRegimeState>(states);
        for (var s = 0; s < states; s++)
        {
            var selfP = hmm.TransitionMatrix[s][s];
            var expectedDuration = selfP < 1.0 ? 1.0 / (1.0 - selfP) : double.PositiveInfinity;
            stateSummaries.Add(new MarkovRegimeState(
                Index: s,
                Label: labels[s],
                MeanLogReturn: hmm.Means[s],
                Variance: hmm.Variances[s],
                StationaryProbability: stationary[s],
                OccupancyProbability: occupancy[s],
                ExpectedDurationBars: expectedDuration));
        }

        var series = new List<MarkovRegimePoint>(returns.Length);
        for (var i = 0; i < returns.Length; i++)
        {
            var bar = bars[i + 1]; // returns[i] corresponds to bars[i+1]
            series.Add(new MarkovRegimePoint(
                TimeUtc: bar.TimestampUtc,
                Close: bar.Close,
                LogReturn: returns[i],
                State: path[i],
                Label: labels[path[i]],
                Posterior: posteriors[i]));
        }

        return new MarkovRegimeResult(
            StateCount: states,
            States: stateSummaries,
            TransitionMatrix: hmm.TransitionMatrix,
            InitialProbabilities: hmm.InitialProbabilities,
            Series: series,
            LogLikelihood: hmm.LogLikelihood,
            Iterations: hmm.Iterations,
            Converged: hmm.Converged);
    }

    /// <summary>Maps each state index to a label by the rank of its mean: the single lowest-mean
    /// state is Bearish, the single highest is Bullish, everything else Neutral.</summary>
    private static RegimeLabel[] LabelByMean(double[] means)
    {
        var order = Enumerable.Range(0, means.Length).OrderBy(i => means[i]).ToArray();
        var labels = new RegimeLabel[means.Length];
        for (var rank = 0; rank < order.Length; rank++)
        {
            var stateIdx = order[rank];
            labels[stateIdx] = rank == 0 ? RegimeLabel.Bearish
                : rank == order.Length - 1 ? RegimeLabel.Bullish
                : RegimeLabel.Neutral;
        }
        return labels;
    }
}
