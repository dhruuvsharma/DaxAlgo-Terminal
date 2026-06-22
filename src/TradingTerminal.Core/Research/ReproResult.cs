namespace TradingTerminal.Core.Research;

/// <summary>
/// The outcome of a sandbox reproduction. Carries the declared output artifacts (with sha256), the
/// resolved environment hash, an optional cost extrapolation, and the full provenance triple
/// (paper id + repo commit + env hash). Provenance is never stripped — it rides through the bridge
/// onto every <see cref="ReproducedSignal"/>.
///
/// <para>Failures fold into this record via <see cref="Failed"/> — the sandbox/ingest seams never
/// throw across the boundary (same never-throw contract as <c>IAiAnalystClient</c>).</para>
/// </summary>
public sealed record ReproResult(
    bool Success,
    string PaperArxivId,
    string RepoCommit,
    EnvHash EnvHash,
    IReadOnlyList<ReproArtifact> Artifacts,
    ReplicationCostEstimate? CostEstimate,
    string? Error)
{
    /// <summary>A failed result carrying the reason and (where known) the provenance triple.</summary>
    public static ReproResult Failed(string reason, string? paperArxivId = null, string? repoCommit = null) =>
        new(
            Success: false,
            PaperArxivId: paperArxivId ?? string.Empty,
            RepoCommit: repoCommit ?? string.Empty,
            EnvHash: EnvHash.None,
            Artifacts: Array.Empty<ReproArtifact>(),
            CostEstimate: null,
            Error: reason);
}
