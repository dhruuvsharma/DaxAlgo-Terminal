using System.Collections.ObjectModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Research;
using TradingTerminal.Infrastructure.Backtest.Strategies;
using TradingTerminal.Infrastructure.Threading;
using TradingTerminal.UI;

namespace TradingTerminal.Ai.PaperLab;

/// <summary>
/// View-model for the Paper Lab tool window. Thin client over the already-built Core seams:
/// <see cref="IPaperIngestClient"/> for paper/repo resolution, and <see cref="IReproOrchestrator"/>
/// for job lifecycle. Does not re-implement backend logic.
///
/// <para>Memory-safety contract:
/// <list type="bullet">
///   <item>The <c>IReproOrchestrator.JobUpdates</c> subscription is the ONLY live resource.
///         It is disposed in <see cref="Dispose"/>.</item>
///   <item>Updates are coalesced through a 250 ms Buffer on the Rx pipeline before marshalling
///         to the UI thread — so rapid status ticks (Resolving → Building → RunningMinimal)
///         do not cause per-tick UI churn.</item>
///   <item>Batches are marshalled via <see cref="IUiDispatcher.Post"/> — no Dispatcher.Invoke
///         in the VM (architectural rule 3).</item>
///   <item>The <see cref="ObservableCollection{T}"/> is bounded by terminal-job pruning
///         (<c>MaxDisplayedJobs</c>) so it cannot grow without bound.</item>
///   <item>No per-window log panel — errors route to <see cref="_logger"/> which feeds the
///         shared <c>InMemoryLogSink</c> Activity Log.</item>
/// </list>
/// </para>
/// </summary>
public sealed partial class PaperLabViewModel : ViewModelBase, IDisposable
{
    private const int MaxDisplayedJobs = 50;

    private readonly IPaperIngestClient _ingestClient;
    private readonly IReproOrchestrator _orchestrator;
    private readonly IReproStrategyRegistrar _registrar;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly ILogger<PaperLabViewModel> _logger;

    private readonly IDisposable _jobUpdatesSubscription;
    private CancellationTokenSource? _resolveCts;
    private CancellationTokenSource? _submitCts;
    private CancellationTokenSource? _registerCts;

    // ── Observable properties ────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResolveCommand))]
    private string _paperUrl = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(ResolveCommand))]
    private bool _isResolving;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsBusy))] private bool _isSubmitting;
    [ObservableProperty] private string? _statusMessage;

    /// <summary>True when any async operation is in-flight. Drives the progress ring.</summary>
    public bool IsBusy => IsResolving || IsSubmitting || IsRegistering;
    [ObservableProperty] private string? _errorMessage;

    // Resolved paper info
    [ObservableProperty] private string? _resolvedPaperTitle;
    [ObservableProperty] private string? _resolvedPaperArxivId;
    [ObservableProperty] private bool _paperResolved;

    // Selected repo for submission
    [ObservableProperty] private RepoRef? _selectedRepo;

    // ── Confidence readout (populated after a successful "Save as strategy") ──

    /// <summary>Overall replication confidence score in [0, 1]. Zero when no registration has run yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfidence))]
    private double _confidenceScore;

    /// <summary>Formatted overall score label (e.g. "Replication confidence: 78 %").</summary>
    [ObservableProperty] private string _confidenceLabel = string.Empty;

    /// <summary>True when a registration result with confidence data is available to display.</summary>
    public bool HasConfidence => ConfidenceScore > 0;

    /// <summary>Per-component confidence rows bound to the readout panel.</summary>
    public ObservableCollection<ReplicationConfidenceComponentViewModel> ConfidenceComponents { get; } = new();

    // ── Save-as-strategy flag ─────────────────────────────────────────────────

    /// <summary>True while a RegisterAsync call is in-flight. Drives the busy indicator on the button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isRegistering;

    public ObservableCollection<RepoRef> CandidateRepos { get; } = new();
    public ObservableCollection<ReproJobRowViewModel> Jobs { get; } = new();

    // Backing resolved paper ref — not bound directly (use named properties above)
    private PaperRef? _resolvedPaper;

    // ── Constructor ──────────────────────────────────────────────────────────

    public PaperLabViewModel(
        IPaperIngestClient ingestClient,
        IReproOrchestrator orchestrator,
        IReproStrategyRegistrar registrar,
        IUiDispatcher uiDispatcher,
        ILogger<PaperLabViewModel> logger)
    {
        _ingestClient = ingestClient;
        _orchestrator = orchestrator;
        _registrar    = registrar;
        _uiDispatcher = uiDispatcher;
        _logger       = logger;

        // Seed Jobs from any ActiveJobs snapshot (jobs that survived an app restart).
        foreach (var job in _orchestrator.ActiveJobs)
            UpsertJobRow(job);

        // Subscribe to the live update stream.
        //
        // Buffer(250 ms) groups updates that arrive within the same quarter-second window into a
        // single IList<ReproJob>. This is the coalescing step — a burst of rapid status transitions
        // (Queued → Resolving → Building) is collapsed into one UI update rather than a separate
        // marshal+redraw per transition.
        //
        // The batch callback posts to the UI thread via IUiDispatcher.Post (architectural rule 3 —
        // no Dispatcher.Invoke in the VM; marshalling is one-layer, done here at the boundary).
        _jobUpdatesSubscription = _orchestrator.JobUpdates
            .Buffer(TimeSpan.FromMilliseconds(250))
            .Where(static batch => batch.Count > 0)
            .Subscribe(
                batch => _uiDispatcher.Post(() =>
                {
                    foreach (var job in batch)
                        UpsertJobRow(job);
                    PruneTerminalJobs();
                }),
                ex => _logger.LogError(ex, "Paper Lab: job update stream terminated unexpectedly"));

        // Initialise availability message.
        StatusMessage = _ingestClient.IsAvailable
            ? "Enter a paper URL and click Resolve."
            : "Paper-lab sidecar unavailable — enable in Settings → Research.";
    }

    // ── Availability ─────────────────────────────────────────────────────────

    public bool IsAvailable => _ingestClient.IsAvailable;

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve the paper URL via <see cref="IPaperIngestClient.ResolveAsync"/>. Populates
    /// <see cref="CandidateRepos"/> and the resolved-paper display properties.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanResolve))]
    public async Task ResolveAsync()
    {
        if (!CanResolve()) return;

        IsResolving = true;
        ErrorMessage = null;
        StatusMessage = "Resolving paper…";
        PaperResolved = false;
        CandidateRepos.Clear();
        _resolvedPaper = null;
        ResolvedPaperTitle = null;
        ResolvedPaperArxivId = null;
        SelectedRepo = null;

        _resolveCts?.Cancel();
        _resolveCts?.Dispose();
        _resolveCts = new CancellationTokenSource();
        var ct = _resolveCts.Token;

        try
        {
            var result = await _ingestClient.ResolveAsync(PaperUrl.Trim(), ct);

            if (!result.Resolved || result.Paper is null)
            {
                ErrorMessage = result.Error ?? "Paper could not be resolved.";
                StatusMessage = "Resolution failed.";
                _logger.LogWarning("Paper resolution failed for {Url}: {Error}", PaperUrl, result.Error);
                return;
            }

            _resolvedPaper = result.Paper;
            ResolvedPaperTitle = result.Paper.Title;
            ResolvedPaperArxivId = result.Paper.ArxivId;
            PaperResolved = true;

            foreach (var repo in result.Repos)
                CandidateRepos.Add(repo);

            SelectedRepo = CandidateRepos.Count > 0 ? CandidateRepos[0] : null;

            StatusMessage = result.Repos.Count == 0
                ? $"Resolved: {result.Paper.Title} — no candidate repos found."
                : $"Resolved: {result.Paper.Title} — {result.Repos.Count} repo(s) found.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Resolution cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Paper Lab: ResolveAsync failed for {Url}", PaperUrl);
            ErrorMessage = $"Resolution error: {ex.Message}";
            StatusMessage = "Resolution failed.";
        }
        finally
        {
            IsResolving = false;
            _resolveCts?.Dispose();
            _resolveCts = null;
        }
    }

    private bool CanResolve() =>
        !IsResolving && !string.IsNullOrWhiteSpace(PaperUrl) && _ingestClient.IsAvailable;

    /// <summary>
    /// Build a <see cref="ReproSpec"/> from the resolved paper + selected repo and submit it via
    /// <see cref="IReproOrchestrator.SubmitAsync"/>. If the orchestrator finds a cached succeeded
    /// job for the spec's cache key it is returned immediately without spawning a new container.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSubmit))]
    public async Task SubmitAsync()
    {
        if (!CanSubmit()) return;

        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = "Submitting reproduction job…";

        _submitCts?.Cancel();
        _submitCts?.Dispose();
        _submitCts = new CancellationTokenSource();
        var ct = _submitCts.Token;

        try
        {
            // Minimal spec — no extra config knobs at this stage; the backend chooses
            // minimal-vs-full based on SandboxPolicy budget.
            var spec = ReproSpec.Minimal(_resolvedPaper!, SelectedRepo!);
            var job = await _orchestrator.SubmitAsync(spec, ct);

            // The job will arrive on JobUpdates so UpsertJobRow will be called reactively;
            // do it eagerly too so the row appears immediately before the first update tick.
            UpsertJobRow(job);

            StatusMessage = job.Status == ReproStatus.Succeeded
                ? $"Cache hit — job {job.Id:N} already succeeded."
                : $"Job {job.Id:N} queued.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Submission cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Paper Lab: SubmitAsync failed");
            ErrorMessage = $"Submission error: {ex.Message}";
            StatusMessage = "Submission failed.";
        }
        finally
        {
            IsSubmitting = false;
            _submitCts?.Dispose();
            _submitCts = null;
        }
    }

    private bool CanSubmit() =>
        !IsSubmitting && PaperResolved && _resolvedPaper is not null && SelectedRepo is not null;

    /// <summary>Cancel a running or queued job.</summary>
    [RelayCommand]
    public async Task CancelJobAsync(Guid jobId)
    {
        try
        {
            await _orchestrator.CancelAsync(jobId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Paper Lab: CancelAsync failed for job {JobId}", jobId);
            ErrorMessage = $"Cancel failed: {ex.Message}";
        }
    }

    /// <summary>Cancel the in-flight Resolve call.</summary>
    [RelayCommand]
    public void CancelResolve() => _resolveCts?.Cancel();

    /// <summary>
    /// Bridge the succeeded job's <see cref="ReproResult"/> into a paper-tagged
    /// <see cref="TradingTerminal.Core.Backtest.BacktestStrategyOption"/>, score replication
    /// confidence, and register it into the live <c>IBacktestStrategyRegistry</c> so it appears
    /// in the Backtest Studio catalog.
    ///
    /// <para>CanExecute: only when the row's <see cref="ReproJobRowViewModel.CanSaveAsStrategy"/>
    /// is true (Status == Succeeded &amp;&amp; Result is not null) and no registration is already
    /// in-flight.</para>
    ///
    /// <para>The registrar never throws — failure is surfaced via
    /// <see cref="ReproRegistration.Error"/> and written to <see cref="ErrorMessage"/>.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveAsStrategy))]
    public async Task SaveAsStrategyAsync(ReproJobRowViewModel row)
    {
        if (!CanSaveAsStrategy(row)) return;

        IsRegistering = true;
        ErrorMessage  = null;
        StatusMessage = $"Registering strategy for \"{row.PaperTitle}\"...";

        _registerCts?.Cancel();
        _registerCts?.Dispose();
        _registerCts = new CancellationTokenSource();
        var ct = _registerCts.Token;

        try
        {
            var registration = await _registrar.RegisterAsync(row.Result!, ct).ConfigureAwait(false);

            // Marshal the result update to the UI thread (architectural rule 3).
            _uiDispatcher.Post(() => ApplyRegistrationResult(row, registration));
        }
        catch (OperationCanceledException)
        {
            _uiDispatcher.Post(() => StatusMessage = "Save cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Paper Lab: SaveAsStrategyAsync failed for job {JobId}", row.JobId);
            _uiDispatcher.Post(() =>
            {
                ErrorMessage  = $"Registration error: {ex.Message}";
                StatusMessage = "Save failed.";
            });
        }
        finally
        {
            // ConfigureAwait(false) means we may be on a thread pool thread; post the flag flip
            // to ensure IsRegistering is cleared on the UI thread so IsBusy raises correctly.
            _uiDispatcher.Post(() =>
            {
                IsRegistering = false;
                _registerCts?.Dispose();
                _registerCts = null;
            });
        }
    }

    private bool CanSaveAsStrategy(ReproJobRowViewModel? row) =>
        !IsRegistering && row is not null && row.CanSaveAsStrategy;

    /// <summary>
    /// Apply a <see cref="ReproRegistration"/> result to the UI — must be called on the UI thread.
    /// Updates status message, error message, and the confidence readout panel.
    /// </summary>
    private void ApplyRegistrationResult(ReproJobRowViewModel row, ReproRegistration registration)
    {
        if (registration.Success)
        {
            var score = registration.Confidence.Score;
            StatusMessage  = $"Saved as strategy — open the Backtest Studio to run it. " +
                             $"Replication confidence: {score * 100:F0} %";
            ErrorMessage   = null;

            // Update confidence readout.
            ConfidenceScore = score;
            ConfidenceLabel = $"Replication confidence: {score * 100:F0} %";
            ConfidenceComponents.Clear();
            foreach (var (name, componentScore) in registration.Confidence.Components)
                ConfidenceComponents.Add(new ReplicationConfidenceComponentViewModel(name, componentScore));

            _logger.LogInformation(
                "Paper Lab: registered strategy for job {JobId} (confidence {Score:0.00})",
                row.JobId, score);
        }
        else
        {
            ErrorMessage  = registration.Error ?? "Registration failed.";
            StatusMessage = "Save failed.";
            _logger.LogWarning(
                "Paper Lab: strategy registration failed for job {JobId}: {Error}",
                row.JobId, registration.Error);
        }
    }

    // ── Job list helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Upsert a job snapshot into the <see cref="Jobs"/> collection. Must be called on the UI
    /// thread — callers from the subscription are marshalled via <c>IUiDispatcher.Post</c>;
    /// direct callers (SubmitAsync, seeding loop) already run on the UI thread.
    /// </summary>
    private void UpsertJobRow(ReproJob job)
    {
        var existing = FindJobRow(job.Id);
        if (existing is not null)
        {
            existing.Update(job);
        }
        else
        {
            Jobs.Insert(0, new ReproJobRowViewModel(job));
        }
    }

    private ReproJobRowViewModel? FindJobRow(Guid id)
    {
        foreach (var row in Jobs)
            if (row.JobId == id) return row;
        return null;
    }

    /// <summary>
    /// Prune terminal jobs when the list exceeds <see cref="MaxDisplayedJobs"/> to prevent
    /// unbounded growth during long-running sessions.
    /// </summary>
    private void PruneTerminalJobs()
    {
        while (Jobs.Count > MaxDisplayedJobs)
        {
            // Remove the oldest terminal job from the bottom.
            bool removed = false;
            for (int i = Jobs.Count - 1; i >= 0; i--)
            {
                if (Jobs[i].IsTerminal)
                {
                    Jobs.RemoveAt(i);
                    removed = true;
                    break;
                }
            }

            // Safety: if all are non-terminal (should not happen given bounded orchestrator
            // concurrency) remove the last entry to prevent an infinite loop.
            if (!removed)
                Jobs.RemoveAt(Jobs.Count - 1);
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <summary>
    /// Dispose all subscriptions and in-flight operations. Called by <c>ToolHostWindow</c>
    /// on close. The <see cref="_jobUpdatesSubscription"/> is the single live resource that
    /// must not outlive the window — disposing it stops all further marshals to the UI thread.
    /// </summary>
    public void Dispose()
    {
        _resolveCts?.Cancel();
        _resolveCts?.Dispose();
        _resolveCts = null;

        _submitCts?.Cancel();
        _submitCts?.Dispose();
        _submitCts = null;

        _registerCts?.Cancel();
        _registerCts?.Dispose();
        _registerCts = null;

        // Disposing the IObservable<ReproJob> subscription stops the Buffer timer and prevents
        // any further Post calls to the UI thread after the window is gone.
        _jobUpdatesSubscription.Dispose();
    }
}
