namespace TradingTerminal.Core.Updates;

/// <summary>How the attempt to hand off to the installer ended.</summary>
public enum UpdateInstallOutcome
{
    /// <summary>The installer process is running. The caller must now shut the application down.</summary>
    Started,
    /// <summary>Automatic installing is switched off.</summary>
    NotAllowed,
    /// <summary>The staged file is gone, or no longer verifies. Nothing was started.</summary>
    Untrusted,
    /// <summary>The process could not be started at all.</summary>
    Failed,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Detail">Why it failed, for the log.</param>
public sealed record UpdateInstallResult(UpdateInstallOutcome Outcome, string? Detail = null)
{
    public bool Started => Outcome == UpdateInstallOutcome.Started;
}

/// <summary>
/// Starts a downloaded, verified installer.
///
/// <para>It deliberately does NOT shut the application down: a component that both spawns a process
/// and terminates the host is untestable and owns a decision that belongs to the shell. It reports
/// <see cref="UpdateInstallOutcome.Started"/> and the shell exits through its normal path, so the
/// generic host still stops and the execution ledger still flushes before the files are replaced.</para>
///
/// <para>Implementations re-verify the file immediately before launching. The gap between download
/// and launch is small and the staging directory is per-user, but re-checking a signature costs
/// nothing and closes a swap between the two.</para>
/// </summary>
public interface IUpdateInstaller
{
    /// <summary>True when this build is willing to run an installer at all.</summary>
    bool CanInstall { get; }

    /// <summary>Launches <paramref name="installerPath"/> silently and asks for the app to be closed.
    /// Never throws.</summary>
    UpdateInstallResult Launch(string installerPath);
}
