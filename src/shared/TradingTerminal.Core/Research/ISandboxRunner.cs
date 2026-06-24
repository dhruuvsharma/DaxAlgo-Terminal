namespace TradingTerminal.Core.Research;

/// <summary>
/// Runs untrusted paper code inside a disposable, deny-by-default sandbox. This is a security
/// boundary: the runner spawns the <em>container/VM CLI</em>, NEVER the paper's entrypoint in-process,
/// and enforces no-host-mounts, <c>--network none</c> (unless the policy allowlist scopes egress),
/// CPU/RAM/pids/disk/wall-clock quotas, and kill-the-process-tree on timeout/cancel.
///
/// <para>Results leave the sandbox only as a declared, sha256-validated artifact file read back from
/// the scratch volume — never via the container's stdout. The runner has no access to the canonical
/// market-data store or broker credentials.</para>
///
/// <para>Implementations MUST NOT throw across this boundary — every failure (Docker absent, timeout,
/// non-zero exit, missing artifact) folds into <see cref="ReproResult.Failed"/>, same never-throw
/// contract as <c>IAiAnalystClient</c>.</para>
/// </summary>
public interface ISandboxRunner
{
    /// <summary>Which backend this runner implements.</summary>
    SandboxKind Kind { get; }

    /// <summary>True when the backend's CLI is present and usable on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Clone the repo at its pinned commit and run the reproduction inside the sandbox under the given
    /// quota and policy, streaming progress lines to <paramref name="log"/>. Never throws.
    ///
    /// <para>The optional <paramref name="plan"/> is the sidecar-resolved environment (image + setup
    /// commands + entrypoint + env hash). When supplied, the runner uses the plan's image and entrypoint
    /// (instead of the configured default image and a placeholder entrypoint) and carries the plan's env
    /// hash into the result provenance. When null, the runner falls back to its configured base image.
    /// The plan changes only WHAT runs — never the deny-by-default isolation flags.</para>
    /// </summary>
    Task<ReproResult> RunAsync(
        ReproSpec spec,
        SandboxQuota quota,
        SandboxPolicy policy,
        IProgress<string> log,
        EnvResolutionPlan? plan = null,
        CancellationToken ct = default);
}
