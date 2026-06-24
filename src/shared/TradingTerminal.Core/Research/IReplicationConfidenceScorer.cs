namespace TradingTerminal.Core.Research;

/// <summary>
/// Scores how much we trust a reproduction, producing a <see cref="ReplicationConfidence"/> (0..1 plus
/// a per-component breakdown) from the sandbox <see cref="ReproResult"/> and the bridged
/// <see cref="ReproSignalManifest"/>. Surfaced in the UI so a low-fidelity run is never silently
/// trusted, and carried as provenance alongside the reproduced strategy.
///
/// <para>Pure and deterministic: same inputs → same score. Never throws.</para>
/// </summary>
public interface IReplicationConfidenceScorer
{
    /// <summary>Score a reproduction. A failed result or empty manifest scores low (never throws).</summary>
    ReplicationConfidence Score(ReproResult result, ReproSignalManifest manifest);
}
