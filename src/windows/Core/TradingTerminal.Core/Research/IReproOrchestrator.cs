namespace TradingTerminal.Core.Research;

/// <summary>
/// Drives the reproduction job lifecycle: accept a spec, queue it (bounded concurrency), run it
/// through <c>ISandboxRunner</c>, and stream status. Implementations check the job cache before
/// submitting — an identical spec returns the cached succeeded job instead of spawning a new
/// container.
///
/// <para>Status is exposed as an <see cref="IObservable{T}"/> (Rx) — consumers subscribe, they don't
/// poll, mirroring <c>IObservable&lt;ConnectionState&gt;</c>. Methods fold failures into a
/// <see cref="ReproStatus.Failed"/> job rather than throwing across the boundary.</para>
/// </summary>
public interface IReproOrchestrator
{
    /// <summary>Submit a spec. Returns the cached succeeded job if one exists for the spec's cache key;
    /// otherwise a freshly-queued job. Never throws.</summary>
    Task<ReproJob> SubmitAsync(ReproSpec spec, CancellationToken ct = default);

    /// <summary>Hot stream of job state transitions. Subscribe; do not poll.</summary>
    IObservable<ReproJob> JobUpdates { get; }

    /// <summary>Request cancellation of a running/queued job. No-op if already terminal.</summary>
    Task CancelAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Snapshot of jobs that are not in a terminal state.</summary>
    IReadOnlyList<ReproJob> ActiveJobs { get; }
}
