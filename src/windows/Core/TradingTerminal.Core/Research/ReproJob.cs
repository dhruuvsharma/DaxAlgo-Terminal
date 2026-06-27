namespace TradingTerminal.Core.Research;

/// <summary>
/// A queued, cancellable, status-streamed reproduction. Persisted by <c>IReproJobStore</c> so jobs
/// survive an app restart: any job in a non-terminal <see cref="ReproStatus"/> is requeued by the
/// orchestrator on startup. Immutable — the orchestrator advances a job by emitting a new record
/// (via <c>with</c>) on the status stream.
/// </summary>
public sealed record ReproJob(
    Guid Id,
    ReproSpec Spec,
    ReproStatus Status,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    ReproResult? Result,
    string? Error)
{
    /// <summary>True when the job has reached a terminal state and will not advance further.</summary>
    public bool IsTerminal =>
        Status is ReproStatus.Succeeded or ReproStatus.Failed or ReproStatus.Cancelled;

    /// <summary>A fresh queued job for the given spec.</summary>
    public static ReproJob Create(ReproSpec spec)
    {
        var now = DateTime.UtcNow;
        return new ReproJob(
            Id: Guid.NewGuid(),
            Spec: spec,
            Status: ReproStatus.Queued,
            CreatedUtc: now,
            UpdatedUtc: now,
            Result: null,
            Error: null);
    }

    /// <summary>A copy advanced to <paramref name="status"/> with the update timestamp bumped.</summary>
    public ReproJob With(ReproStatus status, ReproResult? result = null, string? error = null) =>
        this with
        {
            Status = status,
            UpdatedUtc = DateTime.UtcNow,
            Result = result ?? Result,
            Error = error ?? Error,
        };
}
