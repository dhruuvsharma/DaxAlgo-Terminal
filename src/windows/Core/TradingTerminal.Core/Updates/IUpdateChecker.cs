using System;
using System.Threading;
using System.Threading.Tasks;
namespace TradingTerminal.Core.Updates;

/// <summary>How an update check ended.</summary>
public enum UpdateOutcome
{
    /// <summary>No feed URL or no pinned key — the feature is off. Not an error, never surfaced.</summary>
    NotConfigured,
    /// <summary>The feed was read and this build is current (or newer than the feed).</summary>
    UpToDate,
    /// <summary>A newer version is published. This is the only outcome that prompts the user.</summary>
    UpdateAvailable,
    /// <summary>The check could not complete — offline, bad signature, malformed manifest. Logged only.</summary>
    Failed,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Current">The running version.</param>
/// <param name="Available">The published manifest, when one was read successfully.</param>
/// <param name="FromCache">True when served from the offline last-good cache rather than a fresh fetch.</param>
/// <param name="Detail">Why it failed, for the log. Never shown to the user.</param>
public sealed record UpdateCheckResult(
    UpdateOutcome Outcome,
    Version Current,
    UpdateManifest? Available = null,
    bool FromCache = false,
    string? Detail = null)
{
    public bool HasUpdate => Outcome == UpdateOutcome.UpdateAvailable && Available is not null;
}

/// <summary>
/// Reads the signed release feed and reports whether a newer version exists.
///
/// **Detection only, still.** A checker must never download, unpack or execute anything; it answers
/// one question and touches nothing but the manifest. Installing is a separate, explicitly enabled
/// seam — <see cref="IUpdateDownloader"/> and <see cref="IUpdateInstaller"/> — because a self-updater
/// makes the application a remote-code-execution path, and that stays a decision taken deliberately
/// rather than something a version check quietly grows into. Keeping the two apart is what lets
/// <c>AllowAutomaticInstall</c> be switched off and leave a working, link-only prompt behind.
///
/// Implementations never throw, other than propagating <see cref="OperationCanceledException"/> when
/// the caller cancels. Every other failure classifies as <see cref="UpdateOutcome.Failed"/> and is
/// logged, so a dead or hostile feed can never block start-up or crash the shell.
/// </summary>
public interface IUpdateChecker
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}
