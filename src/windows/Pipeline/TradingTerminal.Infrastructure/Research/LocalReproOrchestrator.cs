using System.Collections.Concurrent;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Research;

namespace TradingTerminal.Infrastructure.Research;

/// <summary>
/// In-process reproduction orchestrator: a bounded-concurrency queue over <see cref="ISandboxRunner"/>
/// backed by <see cref="IReproJobStore"/>. Checks the cache before submitting (an identical spec
/// returns the cached succeeded job, no new container), streams status via an Rx <see cref="Subject{T}"/>
/// (subscribe, don't poll), and requeues unfinished jobs from the store on construction so jobs survive
/// an app restart.
///
/// <para>The orchestrator never runs untrusted code itself — it only schedules work onto the sandbox
/// runner and persists job state.</para>
/// </summary>
internal sealed class LocalReproOrchestrator : IReproOrchestrator, IDisposable
{
    private readonly IReproJobStore _store;
    private readonly ISandboxRunner _runner;
    private readonly IEnvResolverClient _envResolver;
    private readonly IOptionsMonitor<SandboxOptions> _sandboxOptions;
    private readonly ILogger<LocalReproOrchestrator> _logger;

    private readonly Subject<ReproJob> _updates = new();
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<Guid, ReproJob> _active = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();

    // Set on Dispose. A fire-and-forget pipeline may still be running after Dispose() returns; it must
    // bail out before touching the disposed semaphore / Subject rather than throwing ObjectDisposed.
    private volatile bool _disposed;

    public LocalReproOrchestrator(
        IReproJobStore store,
        ISandboxRunner runner,
        IEnvResolverClient envResolver,
        IOptionsMonitor<SandboxOptions> sandboxOptions,
        ILogger<LocalReproOrchestrator> logger)
    {
        _store = store;
        _runner = runner;
        _envResolver = envResolver;
        _sandboxOptions = sandboxOptions;
        _logger = logger;
        _slots = new SemaphoreSlim(Math.Max(1, sandboxOptions.CurrentValue.MaxConcurrent));

        RequeueUnfinished();
    }

    public IObservable<ReproJob> JobUpdates => _updates;

    public IReadOnlyList<ReproJob> ActiveJobs => _active.Values.ToList();

    public Task<ReproJob> SubmitAsync(ReproSpec spec, CancellationToken ct = default)
    {
        // Cache hit: an identical spec already succeeded — reuse it, never spawn a new container.
        var cached = _store.FindCached(spec.CacheKey);
        if (cached is not null)
        {
            _logger.LogDebug("Repro cache hit for {Key}", spec.CacheKey);
            return Task.FromResult(cached);
        }

        var job = ReproJob.Create(spec);
        _store.Save(job);
        _active[job.Id] = job;
        Publish(job);

        // Fire-and-forget the pipeline; status flows over JobUpdates. Errors fold into a Failed job.
        _ = Task.Run(() => RunPipelineAsync(job.Id), CancellationToken.None);
        return Task.FromResult(job);
    }

    public Task CancelAsync(Guid jobId, CancellationToken ct = default)
    {
        if (_cancellations.TryGetValue(jobId, out var cts))
        {
            try { cts.Cancel(); } catch { /* already disposed */ }
        }
        else if (_active.TryGetValue(jobId, out var queued) && !queued.IsTerminal)
        {
            // Queued but not yet started: mark cancelled directly.
            Advance(queued, ReproStatus.Cancelled, error: "Cancelled before start.");
        }
        return Task.CompletedTask;
    }

    private void RequeueUnfinished()
    {
        try
        {
            foreach (var job in _store.LoadUnfinished())
            {
                // Reset to Queued so the pipeline picks it up cleanly after a restart.
                var requeued = job.With(ReproStatus.Queued);
                _store.Save(requeued);
                _active[requeued.Id] = requeued;
                Publish(requeued);
                _ = Task.Run(() => RunPipelineAsync(requeued.Id), CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to requeue unfinished repro jobs on startup");
        }
    }

    private async Task RunPipelineAsync(Guid jobId)
    {
        // A pipeline scheduled before Dispose() can still be entering here afterwards — don't touch the
        // disposed semaphore/Subject.
        if (_disposed) return;
        if (!_active.TryGetValue(jobId, out var job) || job.IsTerminal) return;

        using var cts = new CancellationTokenSource();
        _cancellations[jobId] = cts;

        if (_disposed) return;

        // Wait for a sandbox slot, but honour cancellation so a job cancelled WHILE queued (behind a
        // busy slot) wakes immediately instead of blocking until the slot frees. We track whether the
        // slot was actually acquired so the finally block only releases what it took.
        var acquired = false;
        try
        {
            await _slots.WaitAsync(cts.Token).ConfigureAwait(false);
            acquired = true;
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) Advance(job, ReproStatus.Cancelled, error: "Cancelled before start.");
            _cancellations.TryRemove(jobId, out _);
            return;
        }

        try
        {
            if (_disposed || cts.IsCancellationRequested)
            {
                if (!_disposed) Advance(job, ReproStatus.Cancelled, error: "Cancelled.");
                return;
            }

            var progress = new Progress<string>(line => _logger.LogDebug("[repro {Id}] {Line}", jobId, line));

            // Resolving: ask the sidecar to statically resolve the repo → environment plan (image +
            // setup + entrypoint + env hash). The sidecar never runs the repo's code. When no sidecar is
            // configured (Null client) this yields an empty plan and the runner falls back to its
            // configured base image + placeholder entrypoint, so the pipeline still works offline.
            job = Advance(job, ReproStatus.Resolving);
            var planResult = await _envResolver
                .ResolvePlanAsync(job.Spec.Repo, cts.Token)
                .ConfigureAwait(false);
            EnvResolutionPlan? plan = planResult.Resolved ? planResult.Plan : null;
            if (plan is not null)
                _logger.LogDebug("[repro {Id}] resolved env plan image={Image} envHash={Hash}",
                    jobId, plan.Image, plan.EnvHash.Value);

            if (_disposed || cts.IsCancellationRequested)
            {
                if (!_disposed) Advance(job, ReproStatus.Cancelled, error: "Cancelled.");
                return;
            }

            job = Advance(job, ReproStatus.Building);

            var quota = _sandboxOptions.CurrentValue.ToQuota();
            var policy = SandboxPolicy.DenyAll with { Quota = quota };

            job = Advance(job, ReproStatus.RunningMinimal);
            var result = await _runner
                .RunAsync(job.Spec, quota, policy, progress, plan, cts.Token)
                .ConfigureAwait(false);

            if (cts.IsCancellationRequested)
            {
                Advance(job, ReproStatus.Cancelled, error: "Cancelled.");
                return;
            }

            Advance(job,
                result.Success ? ReproStatus.Succeeded : ReproStatus.Failed,
                result: result,
                error: result.Success ? null : result.Error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Repro pipeline crashed for {Id}", jobId);
            Advance(job, ReproStatus.Failed, error: ex.Message);
        }
        finally
        {
            // Release only the slot we actually acquired, and only if the semaphore isn't disposed.
            if (acquired && !_disposed)
            {
                try { _slots.Release(); } catch (ObjectDisposedException) { /* disposed mid-run */ }
            }
            _cancellations.TryRemove(jobId, out _);
        }
    }

    private ReproJob Advance(ReproJob job, ReproStatus status, ReproResult? result = null, string? error = null)
    {
        var next = job.With(status, result, error);
        _store.Save(next);
        if (next.IsTerminal) _active.TryRemove(next.Id, out _);
        else _active[next.Id] = next;
        Publish(next);
        return next;
    }

    private void Publish(ReproJob job)
    {
        if (_disposed) return;
        // Subscriber faults — and a Subject disposed concurrently — must not break the pipeline.
        try { _updates.OnNext(job); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        // Flip the flag first so any in-flight fire-and-forget pipeline bails before touching the
        // semaphore / Subject we're about to dispose.
        _disposed = true;
        foreach (var cts in _cancellations.Values)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { /* best effort */ }
        }
        _updates.Dispose();
        _slots.Dispose();
    }
}
