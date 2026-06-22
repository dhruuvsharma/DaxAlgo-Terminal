namespace TradingTerminal.Core.Research;

/// <summary>
/// How much we trust a reproduction, as a score in [0, 1] plus the per-component sub-scores that
/// produced it (e.g. "env_resolved", "minimal_metrics_match", "repo_pinned"). Surfaced in the UI so
/// a low-fidelity run is never silently trusted.
/// </summary>
public sealed record ReplicationConfidence(
    double Score,
    IReadOnlyDictionary<string, double> Components)
{
    /// <summary>No confidence (e.g. a failed or not-yet-scored reproduction).</summary>
    public static ReplicationConfidence None =>
        new(0.0, new Dictionary<string, double>());
}
