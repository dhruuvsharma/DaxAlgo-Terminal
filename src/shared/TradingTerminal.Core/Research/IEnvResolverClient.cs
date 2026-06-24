namespace TradingTerminal.Core.Research;

/// <summary>
/// The Core-side seam to the Python sidecar's environment-resolution endpoint
/// (<c>/research/plan</c>). Given a repo + pinned commit, the sidecar shallow-clones the repo, reads
/// its manifest files (Dockerfile / requirements.txt / pyproject / environment.yml) and STATICALLY
/// produces an <see cref="EnvResolutionPlan"/> — it never executes the repo's code.
///
/// <para>One implementation calls the sidecar over HTTP (loopback only); the Null implementation always
/// returns an empty plan so the app builds/runs with no sidecar. Implementations MUST NOT throw across
/// this boundary — no sidecar, an unreachable endpoint, or an unresolvable repo all fold into
/// <see cref="MinimalReproPlan.Empty"/>. Same never-throw contract as <c>IPaperIngestClient</c> /
/// <c>IAiAnalystClient</c>.</para>
/// </summary>
public interface IEnvResolverClient
{
    /// <summary>True when this client expects the sidecar to be reachable (Http when enabled + URL
    /// set; Null returns false). The per-call timeout decides actual success.</summary>
    bool IsAvailable { get; }

    /// <summary>Resolve a repo at a pinned commit to a runnable <see cref="EnvResolutionPlan"/>.
    /// Never throws — failures surface as <see cref="MinimalReproPlan.Empty"/>.</summary>
    Task<MinimalReproPlan> ResolvePlanAsync(RepoRef repo, CancellationToken ct = default);
}
