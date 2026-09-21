using System;
using System.Threading;
using System.Threading.Tasks;

namespace TradingTerminal.Core.Updates;

/// <summary>How far a download has got. <see cref="TotalBytes"/> is null when the server sent no
/// content length, in which case only a spinner is honest.</summary>
public readonly record struct UpdateDownloadProgress(long BytesReceived, long? TotalBytes)
{
    /// <summary>0..1, or null when the total is unknown.</summary>
    public double? Fraction =>
        TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1) : null;
}

/// <summary>Why a download ended. Everything but <see cref="Ok"/> leaves nothing on disk.</summary>
public enum UpdateDownloadOutcome
{
    /// <summary>The installer is downloaded, hash-matched and Authenticode-valid.</summary>
    Ok,
    /// <summary>Automatic installing is switched off, or this build cannot tell what version it is.</summary>
    NotAllowed,
    /// <summary>The manifest does not describe a usable download (no https URL, no/!64-hex sha256).</summary>
    NotOffered,
    /// <summary>Transport failure — unreachable, non-success status, or larger than the cap.</summary>
    Failed,
    /// <summary>The bytes do not hash to the value the signed manifest promised.</summary>
    HashMismatch,
    /// <summary>Unsigned, a broken Authenticode signature, or the wrong signer.</summary>
    UntrustedInstaller,
    /// <summary>The user cancelled, or the app is shutting down.</summary>
    Cancelled,
}

/// <param name="Outcome">What happened.</param>
/// <param name="InstallerPath">The verified installer on disk. Non-null only for <see cref="UpdateDownloadOutcome.Ok"/>.</param>
/// <param name="Detail">Why it failed, for the log. Never shown verbatim to the user.</param>
public sealed record UpdateDownloadResult(
    UpdateDownloadOutcome Outcome,
    string? InstallerPath = null,
    string? Detail = null)
{
    public bool IsReadyToInstall => Outcome == UpdateDownloadOutcome.Ok && !string.IsNullOrEmpty(InstallerPath);
}

/// <summary>
/// Fetches the installer a verified <see cref="UpdateManifest"/> points at, and refuses to hand back
/// anything it could not prove is the release the signer published.
///
/// <para>This is the second half of the update service, and the half that makes the app capable of
/// running new code. <see cref="IUpdateChecker"/> long carried a note that a self-updater was a
/// deliberate decision rather than a natural extension of a version check — this is that decision,
/// taken with the guard rails that note demanded:</para>
/// <list type="number">
///   <item><description>the download URL must be absolute <c>https</c>;</description></item>
///   <item><description>the downloaded bytes must hash to the <c>sha256</c> in the SIGNED manifest —
///   a manifest signature proves where the URL came from, never that the file behind it is intact;</description></item>
///   <item><description>the file must carry a valid Authenticode signature, checked through
///   <c>WinVerifyTrust</c>, optionally pinned to our own certificate thumbprint.</description></item>
/// </list>
///
/// <para>Implementations never throw except to propagate <see cref="OperationCanceledException"/>.
/// Any other failure classifies, is logged, and leaves no partial file behind, so a dead or hostile
/// release host degrades to "the banner still offers the release-notes link".</para>
/// </summary>
public interface IUpdateDownloader
{
    /// <summary>True when this build is willing to install at all. False keeps the banner link-only,
    /// so the view can hide an "Update now" button that would only ever fail.</summary>
    bool CanInstall { get; }

    Task<UpdateDownloadResult> DownloadAsync(
        UpdateManifest manifest,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
