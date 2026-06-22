namespace TradingTerminal.Core.Research;

/// <summary>
/// Maps a sandbox <see cref="ReproResult"/>'s declared artifact onto a canonical, InstrumentId-keyed
/// <see cref="ReproSignalManifest"/> — the only path the reproduced output takes into the engine
/// (data/signals only, no live orders). Reads and validates the declared <c>result.json</c> artifact
/// (schema documented on <see cref="ReproSignalManifest"/>); never executes any of the untrusted code.
///
/// <para>Provenance (paper id + repo commit + env hash) from the result rides onto the manifest header
/// and every mapped signal. Implementations MUST NOT throw across this boundary — a missing/invalid
/// artifact yields <see cref="ReproSignalManifest.Empty"/>.</para>
/// </summary>
public interface IReproSignalBridge
{
    /// <summary>Map a succeeded result's declared artifact to a time-sorted, InstrumentId-keyed
    /// manifest. Never throws — returns an empty manifest (with whatever provenance is known) on any
    /// failure (no artifact, unreadable, malformed JSON, provenance mismatch, no usable rows).</summary>
    Task<ReproSignalManifest> MapAsync(ReproResult result, CancellationToken ct = default);
}
