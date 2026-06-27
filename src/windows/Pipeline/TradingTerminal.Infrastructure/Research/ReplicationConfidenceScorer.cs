using TradingTerminal.Core.Research;

namespace TradingTerminal.Infrastructure.Research;

/// <summary>
/// Default <see cref="IReplicationConfidenceScorer"/>: a transparent weighted average of independent
/// fidelity components, each in [0, 1], so the UI can show exactly why a run scored what it did. Pure
/// and deterministic — same inputs always yield the same score. Never throws.
///
/// <para>Components:</para>
/// <list type="bullet">
/// <item><b>build_run_success</b> — the sandbox build/run succeeded at all (1) or failed (0). A failed
/// run caps the whole score, since every other component is meaningless without output.</item>
/// <item><b>signal_completeness</b> — the artifact bridged to a non-empty, parseable manifest (1) or
/// not (0).</item>
/// <item><b>output_determinism</b> — a resolved environment hash is present (1) or absent (0); without
/// it the run isn't reproducible.</item>
/// <item><b>metric_closeness</b> — if the result carries a reported-vs-reproduced metric pair, how close
/// they are within tolerance (1 at exact, decaying to 0 at/over tolerance); 0.5 (neutral) when no metric
/// was reported, so an un-benchmarked reproduction is neither rewarded nor punished for it.</item>
/// </list>
/// </summary>
public sealed class ReplicationConfidenceScorer : IReplicationConfidenceScorer
{
    // Relative weights; normalised at combine time so they need not sum to 1.
    private const double WeightBuildRun = 0.30;
    private const double WeightCompleteness = 0.25;
    private const double WeightDeterminism = 0.20;
    private const double WeightCloseness = 0.25;

    public ReplicationConfidence Score(ReproResult result, ReproSignalManifest manifest)
    {
        var buildRun = result.Success ? 1.0 : 0.0;
        var completeness = manifest.HasSignals ? 1.0 : 0.0;
        var determinism = !result.EnvHash.IsNone ? 1.0 : 0.0;
        var closeness = MetricCloseness(result);

        var components = new Dictionary<string, double>
        {
            ["build_run_success"] = buildRun,
            ["signal_completeness"] = completeness,
            ["output_determinism"] = determinism,
            ["metric_closeness"] = closeness,
        };

        // A failed build/run is fatal — no output to trust. Otherwise the weighted average.
        double score;
        if (buildRun <= 0.0)
        {
            score = 0.0;
        }
        else
        {
            var weighted =
                WeightBuildRun * buildRun +
                WeightCompleteness * completeness +
                WeightDeterminism * determinism +
                WeightCloseness * closeness;
            var totalWeight = WeightBuildRun + WeightCompleteness + WeightDeterminism + WeightCloseness;
            score = Clamp01(weighted / totalWeight);
        }

        return new ReplicationConfidence(score, components);
    }

    /// <summary>
    /// Closeness of a reported-vs-reproduced headline metric, when the result carries one. The current
    /// <see cref="ReproResult"/> does not yet carry a reported/reproduced metric pair (that arrives when
    /// the sidecar emits paper-vs-repro benchmarks), so this returns the neutral 0.5 — an un-benchmarked
    /// run is neither credited nor penalised for a metric it never claimed. When a metric pair is added
    /// to the result, replace this with a linear decay from 1 (exact) to 0 (relative error ≥ tolerance):
    /// <c>1 - |reproduced - reported| / max(|reported|, ε) / tolerance</c>, clamped to [0, 1].
    /// </summary>
    private static double MetricCloseness(ReproResult result)
    {
        _ = result;
        return 0.5;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
