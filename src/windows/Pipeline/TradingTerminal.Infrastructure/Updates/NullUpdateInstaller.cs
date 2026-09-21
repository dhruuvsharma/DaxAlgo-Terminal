using System;
using System.Threading;
using System.Threading.Tasks;
using TradingTerminal.Core.Updates;

namespace TradingTerminal.Infrastructure.Updates;

/// <summary>
/// The downloader and installer registered whenever installing is not available: no feed, no pinned
/// key, <c>AllowAutomaticInstall</c> off, or a build that cannot tell what version it is.
///
/// <para>It serves both seams so the shell resolves the same dependencies either way and the notice
/// view-model needs no "is installing switched on?" branch — the same reason
/// <see cref="NullUpdateChecker"/> doubles as the off-state notifier. <c>CanInstall</c> is false, so
/// the banner simply renders its original link-only shape.</para>
/// </summary>
public sealed class NullUpdateInstaller : IUpdateDownloader, IUpdateInstaller
{
    /// <param name="reason">Why installing is off, for the log. Never shown to the user.</param>
    public NullUpdateInstaller(string reason = "Automatic installing is not available.") => Reason = reason;

    /// <summary>Why this build will not install. Read by the registration's log line.</summary>
    public string Reason { get; }

    public bool CanInstall => false;

    public Task<UpdateDownloadResult> DownloadAsync(
        UpdateManifest manifest,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new UpdateDownloadResult(UpdateDownloadOutcome.NotAllowed, Detail: Reason));

    public UpdateInstallResult Launch(string installerPath) =>
        new(UpdateInstallOutcome.NotAllowed, Reason);
}
