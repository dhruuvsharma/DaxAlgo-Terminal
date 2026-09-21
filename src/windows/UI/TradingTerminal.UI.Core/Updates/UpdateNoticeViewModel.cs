using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradingTerminal.Core.Updates;

namespace TradingTerminal.UI.Updates;

/// <summary>
/// Drives the shell's "a new version is available" strip. Subscribes to <see cref="IUpdateNotifier"/>,
/// marshals onto the UI thread, and remembers what the user dismissed so the same release is never
/// announced twice.
///
/// <para><b>Accepting installs.</b> When the build is configured for it, <c>Update now</c> downloads
/// the installer, verifies it against the hash in the signed manifest and against Authenticode, then
/// launches it silently and raises <see cref="ExitRequested"/> so the shell can close and let the
/// files be replaced. Declining — or simply ignoring the strip — leaves the user on the current
/// version, which is the whole point of prompting rather than updating behind their back.</para>
///
/// <para><b>It degrades to a link.</b> Where installing is not available (no feed, switched off, a
/// build that cannot name its own version), <see cref="CanInstall"/> is false and the strip renders
/// exactly as it always did: release notes and dismiss. Nothing here decides whether installing is
/// allowed; it only reflects what the injected seams report.</para>
///
/// <para>Passive by construction: the check runs on a background service, so a shell that never binds
/// this view-model costs nothing, and one that does is never blocked by a slow or dead feed.</para>
/// </summary>
public sealed partial class UpdateNoticeViewModel : ViewModelBase
{
    private readonly IUpdateNotifier? _notifier;
    private readonly DismissedUpdateStore _dismissed;
    private readonly IUpdateDownloader? _downloader;
    private readonly IUpdateInstaller? _installer;
    private readonly Action? _requestExit;

    private UpdateManifest? _pending;
    private CancellationTokenSource? _install;
    private int _lastReportedPercent = -1;

    /// <summary>Null-safe throughout: a shell with no update registration passes nothing and the strip
    /// stays hidden. <paramref name="dismissed"/> is injectable so tests don't touch the user's
    /// profile; <paramref name="requestExit"/> is how the shell — which owns shutdown — is told to
    /// close once the installer is running.</summary>
    public UpdateNoticeViewModel(
        IUpdateNotifier? notifier = null,
        DismissedUpdateStore? dismissed = null,
        IUpdateDownloader? downloader = null,
        IUpdateInstaller? installer = null,
        Action? requestExit = null)
    {
        _notifier = notifier;
        _dismissed = dismissed ?? DismissedUpdateStore.Default;
        _downloader = downloader;
        _installer = installer;
        _requestExit = requestExit;
        if (notifier is null) return;

        notifier.UpdateAvailable += OnUpdateAvailable;

        // Catch-up: the first check may already have completed before this window was built.
        if (notifier.Latest is { } latest) Apply(latest);
    }

    /// <summary>Raised after the installer has started, asking the shell to shut down so the running
    /// files can be replaced. The shell exits through its normal path, so the generic host still stops
    /// and the execution ledger still flushes.</summary>
    public event Action? ExitRequested;

    /// <summary>Whether the strip is showing. False until a newer, non-dismissed version is found.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>The published version, e.g. <c>1.4.0</c>. Empty while nothing is pending.</summary>
    [ObservableProperty]
    private string _availableVersion = string.Empty;

    /// <summary>Banner copy, e.g. <c>"DaxAlgo Terminal 1.4.0 is available — you're on 1.3.2."</c></summary>
    [ObservableProperty]
    private string _message = string.Empty;

    /// <summary>True when the release-notes link is usable, so the view can hide a dead button.</summary>
    [ObservableProperty]
    private bool _hasReleaseNotes;

    /// <summary>True when this release can actually be installed from here — the seams are live and
    /// the manifest offers a download. False renders the original link-only strip.</summary>
    [ObservableProperty]
    private bool _canInstall;

    /// <summary>True from the moment <c>Update now</c> is pressed until the installer starts or the
    /// attempt fails. The view swaps the buttons for progress while this is set.</summary>
    [ObservableProperty]
    private bool _isInstalling;

    /// <summary>Download progress, 0–100. Meaningless while <see cref="IsProgressIndeterminate"/>.</summary>
    [ObservableProperty]
    private double _progressPercent;

    /// <summary>True when the release host sent no length, so only a marquee is honest.</summary>
    [ObservableProperty]
    private bool _isProgressIndeterminate;

    /// <summary>What is happening, or why it stopped. Empty when there is nothing to say.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    private string _releaseNotesUrl = string.Empty;

    private void OnUpdateAvailable(UpdateCheckResult result) => _ = UiThread.RunAsync(() => Apply(result));

    private void Apply(UpdateCheckResult result)
    {
        if (!result.HasUpdate) return;

        var version = result.Available!.Version;
        if (_dismissed.IsDismissed(version)) return;

        // A second announcement while a download is running would reset the strip under the user.
        if (IsInstalling && string.Equals(version, AvailableVersion, StringComparison.Ordinal)) return;

        _pending = result.Available;
        AvailableVersion = version;
        Message = $"DaxAlgo Terminal {version} is available — you're on {result.Current.ToString(3)}.";

        // Only ever offer an https link: the manifest is signed, but a signed manifest that points at
        // an http URL would still hand the user to a hijackable page.
        _releaseNotesUrl = Uri.TryCreate(result.Available.ReleaseNotesUrl, UriKind.Absolute, out var uri)
                           && uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : string.Empty;
        HasReleaseNotes = _releaseNotesUrl.Length > 0;

        // Both seams must be willing AND the release must actually ship an installer — an
        // announce-only release (docs, a mobile build) carries no download URL.
        CanInstall = _downloader is { CanInstall: true }
                     && _installer is { CanInstall: true }
                     && !string.IsNullOrWhiteSpace(result.Available.DownloadUrl)
                     && !string.IsNullOrWhiteSpace(result.Available.Sha256);

        IsVisible = true;
    }

    /// <summary>
    /// The accept path: download, verify, launch, and ask the shell to close. Everything the user
    /// sees on failure is deliberately vague — the detail goes to the log, because the failure text
    /// comes from a remote host and the release-notes link is the recovery either way.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartInstall))]
    private async Task InstallAsync()
    {
        if (_pending is null || _downloader is null || _installer is null) return;

        var manifest = _pending;
        _install?.Dispose();
        _install = new CancellationTokenSource();
        var token = _install.Token;

        IsInstalling = true;
        _lastReportedPercent = -1;
        ProgressPercent = 0;
        IsProgressIndeterminate = true;
        StatusText = "Preparing download…";
        NotifyCommands();

        UpdateDownloadResult download;
        try
        {
            download = await _downloader
                .DownloadAsync(manifest, new InlineProgress(OnProgress), token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            ResetAfterFailure(string.Empty);
            return;
        }
        catch (Exception ex)
        {
            // The seam is contracted never to throw; if one does it must not take the shell with it.
            Debug.WriteLine($"Update download threw: {ex}");
            ResetAfterFailure("The update could not be downloaded. Use Release notes to update by hand.");
            return;
        }

        if (!download.IsReadyToInstall)
        {
            if (download.Outcome != UpdateDownloadOutcome.Cancelled)
                Debug.WriteLine($"Update download refused: {download.Outcome} — {download.Detail}");
            ResetAfterFailure(DescribeFailure(download.Outcome));
            return;
        }

        StatusText = "Starting the installer…";
        IsProgressIndeterminate = true;

        var launch = _installer.Launch(download.InstallerPath!);
        if (!launch.Started)
        {
            Debug.WriteLine($"Update install refused: {launch.Outcome} — {launch.Detail}");
            ResetAfterFailure("The installer could not be started. Use Release notes to update by hand.");
            return;
        }

        // The installer is running and will replace these very files. Closing is the point.
        StatusText = "Closing to finish the update…";
        ExitRequested?.Invoke();
        _requestExit?.Invoke();
    }

    private bool CanStartInstall() => CanInstall && !IsInstalling;

    /// <summary>Abandons a download in flight. The partial file is deleted by the downloader, and the
    /// strip returns to offering the update again.</summary>
    [RelayCommand(CanExecute = nameof(IsInstalling))]
    private void CancelInstall() => _install?.Cancel();

    /// <summary>
    /// Hands each report straight to <see cref="OnProgress"/> on whatever thread produced it.
    ///
    /// <para><see cref="Progress{T}"/> would be the obvious choice and is the wrong one here. It
    /// captures a <see cref="SynchronizationContext"/> at construction and posts to it — which is a
    /// second hop on top of the one <see cref="OnProgress"/> already makes, and, where there is no
    /// context to capture, becomes a post to the thread pool that can land after the download has
    /// finished. Marshalling is this view-model's job and it does it deliberately, coalesced.</para>
    /// </summary>
    private sealed class InlineProgress(Action<UpdateDownloadProgress> report)
        : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => report(value);
    }

    /// <summary>Progress arrives on a background thread, every buffer. Whole percentage points are
    /// the only changes worth waking the dispatcher for — a few-hundred-megabyte download would
    /// otherwise queue thousands of no-op UI updates.</summary>
    private void OnProgress(UpdateDownloadProgress progress)
    {
        var fraction = progress.Fraction;
        if (fraction is null)
        {
            if (_lastReportedPercent == -1) return;
            _lastReportedPercent = -1;
            _ = UiThread.RunAsync(() => IsProgressIndeterminate = true);
            return;
        }

        var percent = (int)(fraction.Value * 100);
        if (percent == _lastReportedPercent) return;
        _lastReportedPercent = percent;

        _ = UiThread.RunAsync(() =>
        {
            IsProgressIndeterminate = false;
            ProgressPercent = percent;
            StatusText = $"Downloading… {percent}%";
        });
    }

    /// <summary>Vague on purpose. The specifics come from a remote host and go to the log; what the
    /// user needs is whether to wait, retry, or go and do it by hand.</summary>
    private static string DescribeFailure(UpdateDownloadOutcome outcome) => outcome switch
    {
        UpdateDownloadOutcome.Cancelled => string.Empty,
        UpdateDownloadOutcome.HashMismatch or UpdateDownloadOutcome.UntrustedInstaller =>
            "The download didn't match the signed release and was discarded. Use Release notes to update by hand.",
        UpdateDownloadOutcome.NotOffered =>
            "This release has no installer to download. Use Release notes to update by hand.",
        UpdateDownloadOutcome.NotAllowed =>
            "This build doesn't install updates automatically. Use Release notes to update by hand.",
        _ => "The download failed. Check your connection, or use Release notes to update by hand.",
    };

    private void ResetAfterFailure(string status)
    {
        IsInstalling = false;
        IsProgressIndeterminate = false;
        ProgressPercent = 0;
        StatusText = status;
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        InstallCommand.NotifyCanExecuteChanged();
        CancelInstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnCanInstallChanged(bool value) => InstallCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void OpenReleaseNotes()
    {
        if (_releaseNotesUrl.Length == 0) return;
        try
        {
            Process.Start(new ProcessStartInfo(_releaseNotesUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // No default browser, or a policy-blocked shell execute. Nothing to recover — the user
            // can still find the release themselves, and losing a link must not fault the shell.
            Debug.WriteLine($"Failed to open release notes: {ex}");
        }
    }

    /// <summary>Hides the strip and suppresses this version for good — the next release prompts again.
    /// A download in flight is abandoned: dismissing an update the user no longer wants should not
    /// leave it quietly finishing in the background.</summary>
    [RelayCommand]
    private void Dismiss()
    {
        _install?.Cancel();
        _dismissed.Dismiss(AvailableVersion);
        IsVisible = false;
    }

    /// <summary>Unsubscribes and abandons any download in flight. A shell that rebuilds its main
    /// view-model must call this or the notifier, which is an app-lifetime singleton, keeps the dead
    /// view-model — and its download — alive.</summary>
    public void Detach()
    {
        if (_notifier is not null) _notifier.UpdateAvailable -= OnUpdateAvailable;
        ExitRequested = null;
        try { _install?.Cancel(); } catch (ObjectDisposedException) { }
        _install?.Dispose();
        _install = null;
    }
}
