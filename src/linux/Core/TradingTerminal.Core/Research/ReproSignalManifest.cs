using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Research;

/// <summary>
/// A bridged, time-sorted set of <see cref="ReproducedSignal"/>s plus header provenance — the single
/// hand-off from a sandbox reproduction into the backtest engine. Produced by
/// <see cref="IReproSignalBridge.MapAsync"/> from a succeeded <see cref="ReproResult"/>'s declared
/// artifact, and replayed by the <c>ReproducedSignalStrategyKernel</c>. There is NO live order path —
/// signals feed the backtester only.
///
/// <para><b>Provenance.</b> The header carries the full triple (paper id + repo commit + env hash);
/// every <see cref="ReproducedSignal"/> in <see cref="Signals"/> carries the same triple. Never strip
/// it — it is what lets a reproduced strategy trace back to the exact paper/commit/env that produced
/// its alpha, and it drives the clickable paper pill on the strategy card.</para>
///
/// <para><b>On-disk artifact schema</b> (the file the sandbox/sidecar emits as
/// <c>/scratch/result.json</c> and the bridge parses — all keys snake_case):</para>
/// <code>
/// {
///   "paper_id":  "2507.22712",          // arXiv id; MUST match the spec's paper
///   "commit":    "abc123…",             // pinned repo commit; MUST match the spec's repo commit
///   "env_hash":  "sha256:…",            // resolved environment hash (provenance + determinism marker)
///   "signals": [
///     {
///       "instrument_id": 42,            // canonical InstrumentId.Value (int). 0/missing → dropped.
///       "timestamp_utc": "2024-01-01T00:00:05Z",  // ISO-8601 UTC
///       "value":         1.0,           // signed; sign() drives the position target
///       "kind":          "position"     // optional: position | score | weight (default position)
///     }
///     // … time-sorted is preferred but the bridge re-sorts defensively
///   ]
/// }
/// </code>
/// <para>The artifact is the ONLY data-flow path out of the sandbox (read back, sha256-validated). The
/// bridge validates it, normalizes instrument ids onto canonical <see cref="InstrumentId"/>, drops
/// malformed rows, and NEVER throws — a missing/invalid artifact yields <see cref="Empty"/>.</para>
/// </summary>
public sealed record ReproSignalManifest(
    PaperRef Paper,
    string RepoCommit,
    EnvHash EnvHash,
    IReadOnlyList<ReproducedSignal> Signals)
{
    /// <summary>An empty manifest carrying whatever provenance is known — the never-throw failure shape
    /// of the bridge. <see cref="HasSignals"/> is false, so callers can refuse to build a strategy.</summary>
    public static ReproSignalManifest Empty(PaperRef? paper = null, string? commit = null, EnvHash? envHash = null) =>
        new(paper ?? new PaperRef(string.Empty, string.Empty, string.Empty),
            commit ?? string.Empty,
            envHash ?? EnvHash.None,
            Array.Empty<ReproducedSignal>());

    /// <summary>True when at least one usable signal survived validation.</summary>
    public bool HasSignals => Signals.Count > 0;

    /// <summary>The distinct instruments this manifest emits signals for.</summary>
    public IReadOnlyCollection<InstrumentId> Instruments =>
        Signals.Select(s => s.Instrument).Distinct().ToArray();
}
