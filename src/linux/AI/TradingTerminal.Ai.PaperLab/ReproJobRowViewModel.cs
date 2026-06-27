using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Research;
using TradingTerminal.UI;

namespace TradingTerminal.Ai.PaperLab;

/// <summary>
/// Per-row view-model for a <see cref="ReproJob"/> in the Paper Lab job list. Updated in-place
/// (via <see cref="Update"/>) when <c>IReproOrchestrator.JobUpdates</c> emits a new snapshot for
/// the same job id, avoiding a full list rebuild on every status tick.
/// </summary>
public sealed partial class ReproJobRowViewModel : ViewModelBase
{
    public Guid JobId { get; }

    [ObservableProperty] private ReproStatus _status;
    [ObservableProperty] private string _paperTitle = string.Empty;
    [ObservableProperty] private string _repoUrl = string.Empty;
    [ObservableProperty] private string _cacheKey = string.Empty;
    [ObservableProperty] private bool _isCacheHit;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private DateTime _createdUtc;
    [ObservableProperty] private DateTime _updatedUtc;
    [ObservableProperty] private bool _isTerminal;

    /// <summary>
    /// The reproduction result — non-null once the job has <see cref="ReproStatus.Succeeded"/>.
    /// Exposed so <see cref="PaperLabViewModel.SaveAsStrategyCommand"/> can pass it to
    /// <c>IReproStrategyRegistrar.RegisterAsync</c> without re-querying the orchestrator.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveAsStrategy))]
    private ReproResult? _result;

    /// <summary>
    /// True when the job has succeeded and carries a usable result — drives the
    /// <c>SaveAsStrategyCommand</c> CanExecute guard.
    /// </summary>
    public bool CanSaveAsStrategy => Status == ReproStatus.Succeeded && Result is not null;

    public ReproJobRowViewModel(ReproJob job)
    {
        JobId = job.Id;
        ApplyJob(job);
    }

    /// <summary>Apply a fresh job snapshot, updating only the fields that may have changed.</summary>
    public void Update(ReproJob job)
    {
        ApplyJob(job);
        // StatusLabel and CanSaveAsStrategy are computed strings/bools derived from Status — raise
        // them explicitly so bindings update without requiring Status to carry the values itself.
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(CanSaveAsStrategy));
    }

    private void ApplyJob(ReproJob job)
    {
        Status      = job.Status;
        PaperTitle  = job.Spec.Paper.Title;
        RepoUrl     = job.Spec.Repo.GitUrl;
        CacheKey    = job.Spec.CacheKey[..8]; // show first 8 hex chars as a short badge
        IsCacheHit  = job.Result?.Success == true && job.Status == ReproStatus.Succeeded;
        Result      = job.Result;
        Error       = job.Error;
        CreatedUtc  = job.CreatedUtc;
        UpdatedUtc  = job.UpdatedUtc;
        IsTerminal  = job.IsTerminal;
    }

    /// <summary>Human-readable status label for the XAML binding.</summary>
    public string StatusLabel => Status switch
    {
        ReproStatus.Queued        => "Queued",
        ReproStatus.Resolving     => "Resolving",
        ReproStatus.Building      => "Building",
        ReproStatus.RunningMinimal => "Running (minimal)",
        ReproStatus.RunningFull   => "Running (full)",
        ReproStatus.Bridged       => "Bridging signals",
        ReproStatus.Succeeded     => "Succeeded",
        ReproStatus.Failed        => "Failed",
        ReproStatus.Cancelled     => "Cancelled",
        _                         => Status.ToString(),
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Confidence readout row (used by PaperLabViewModel.ConfidenceComponents)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A single named component of a <see cref="ReplicationConfidence"/> breakdown, exposed as a
/// bindable row in <see cref="PaperLabViewModel.ConfidenceComponents"/> so the XAML can render a
/// labelled progress bar per component without any converter.
/// </summary>
public sealed class ReplicationConfidenceComponentViewModel
{
    /// <summary>The component key as returned by the scorer (e.g. "env_resolved").</summary>
    public string Name { get; }

    /// <summary>Score in [0, 1]. Bound directly to a ProgressBar with Maximum="1".</summary>
    public double Score { get; }

    /// <summary>Score formatted as a percentage label (e.g. "78 %").</summary>
    public string ScoreLabel { get; }

    public ReplicationConfidenceComponentViewModel(string name, double score)
    {
        Name       = name;
        Score      = score;
        ScoreLabel = $"{score * 100:F0} %";
    }
}
