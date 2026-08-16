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
/// <para>It offers a link, not a download. The banner sends the user to the release notes and the
/// installer they get there carries its own Authenticode signature — see <see cref="IUpdateChecker"/>
/// for why this deliberately stops short of self-updating.</para>
///
/// <para>Passive by construction: the check runs on a background service, so a shell that never binds
/// this view-model costs nothing, and one that does is never blocked by a slow or dead feed.</para>
/// </summary>
public sealed partial class UpdateNoticeViewModel : ViewModelBase
{
    private readonly IUpdateNotifier? _notifier;
    private readonly DismissedUpdateStore _dismissed;

    /// <summary>Null-safe: a shell with no update registration passes null and the strip stays hidden.
    /// <paramref name="dismissed"/> is injectable so tests don't touch the user's profile.</summary>
    public UpdateNoticeViewModel(IUpdateNotifier? notifier = null, DismissedUpdateStore? dismissed = null)
    {
        _notifier = notifier;
        _dismissed = dismissed ?? DismissedUpdateStore.Default;
        if (notifier is null) return;

        notifier.UpdateAvailable += OnUpdateAvailable;

        // Catch-up: the first check may already have completed before this window was built.
        if (notifier.Latest is { } latest) Apply(latest);
    }

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

    private string _releaseNotesUrl = string.Empty;

    private void OnUpdateAvailable(UpdateCheckResult result) => _ = UiThread.RunAsync(() => Apply(result));

    private void Apply(UpdateCheckResult result)
    {
        if (!result.HasUpdate) return;

        var version = result.Available!.Version;
        if (_dismissed.IsDismissed(version)) return;

        AvailableVersion = version;
        Message = $"DaxAlgo Terminal {version} is available — you're on {result.Current.ToString(3)}.";

        // Only ever offer an https link: the manifest is signed, but a signed manifest that points at
        // an http URL would still hand the user to a hijackable page.
        _releaseNotesUrl = Uri.TryCreate(result.Available.ReleaseNotesUrl, UriKind.Absolute, out var uri)
                           && uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : string.Empty;
        HasReleaseNotes = _releaseNotesUrl.Length > 0;

        IsVisible = true;
    }

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

    /// <summary>Hides the strip and suppresses this version for good — the next release prompts again.</summary>
    [RelayCommand]
    private void Dismiss()
    {
        _dismissed.Dismiss(AvailableVersion);
        IsVisible = false;
    }

    /// <summary>Unsubscribes. A shell that rebuilds its main view-model must call this or the notifier,
    /// which is an app-lifetime singleton, keeps the dead view-model alive.</summary>
    public void Detach()
    {
        if (_notifier is not null) _notifier.UpdateAvailable -= OnUpdateAvailable;
    }
}
