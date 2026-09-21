using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Updates;
using TradingTerminal.Infrastructure.Plugins;

namespace TradingTerminal.Infrastructure.Updates;

/// <summary>
/// Hands a downloaded, verified installer to Inno Setup in silent mode and asks the shell to close.
///
/// <para><b>Why this needs no elevation.</b> The terminal installs per-user, into
/// <c>%LocalAppData%\Programs\DaxAlgo Terminal</c>, with <c>PrivilegesRequired=lowest</c> and a stable
/// <c>AppId</c>. An upgrade therefore replaces files the current user already owns: no UAC prompt, and
/// nothing here ever tries to elevate. An installer that suddenly demanded administrator rights would
/// be a sign the release changed shape, not something to work around.</para>
///
/// <para><b>Why it does not close the app itself.</b> It reports <see cref="UpdateInstallOutcome.Started"/>
/// and leaves shutting down to the shell, so the exit runs the normal WPF path — the generic host
/// stops, the execution ledger flushes — instead of a component deep in Infrastructure killing a
/// process that may be holding trading state. Inno's own Restart Manager closes anything still
/// holding files, so the ordering is safe either way.</para>
/// </summary>
public sealed class SilentSetupUpdateInstaller : IUpdateInstaller
{
    /// <summary>
    /// How the installer is invoked. This is a <b>contract with <c>installer/DaxAlgoTerminal.iss</c></b>
    /// and is public so that coupling is visible and testable rather than buried in a process launch.
    ///
    /// <para><c>/SILENT</c> shows a progress window but no wizard — the user already consented in the
    /// app, and a fully invisible install (<c>/VERYSILENT</c>) looks like a hang on a large release.
    /// <c>/SUPPRESSMSGBOXES</c> keeps a prompt from stranding an unattended upgrade. <c>/NORESTART</c>
    /// forbids the installer rebooting the machine under a trader. <c>/DAXRELAUNCH=1</c> is read by the
    /// .iss's <c>WantsRelaunch</c> check to start the terminal again afterwards: the stock launch entry
    /// is <c>skipifsilent</c>, so without this flag a silent upgrade would leave the user staring at a
    /// closed application.</para>
    /// </summary>
    public static IReadOnlyList<string> SilentArguments { get; } =
        ["/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/DAXRELAUNCH=1"];

    private readonly InstallerTrust _trust;
    private readonly bool _allowed;
    private readonly ILogger<SilentSetupUpdateInstaller> _logger;

    public SilentSetupUpdateInstaller(
        IPluginSignatureInspector signatures,
        bool allowed,
        string? pinnedThumbprint,
        ILogger<SilentSetupUpdateInstaller> logger)
    {
        _trust = new InstallerTrust(signatures, pinnedThumbprint);
        _allowed = allowed;
        _logger = logger;
    }

    public bool CanInstall => _allowed;

    public UpdateInstallResult Launch(string installerPath)
    {
        if (!_allowed)
            return new UpdateInstallResult(UpdateInstallOutcome.NotAllowed,
                "Automatic installing is switched off for this build.");

        if (string.IsNullOrWhiteSpace(installerPath))
            return new UpdateInstallResult(UpdateInstallOutcome.Untrusted, "No installer was staged.");

        // Re-check the signature immediately before executing. The staging directory is per-user and
        // the window since the download is small, but a signature check costs milliseconds and this is
        // the last moment anything can be said about the file that is about to run as the user.
        var untrusted = _trust.Verify(installerPath);
        if (untrusted is not null)
        {
            _logger.LogWarning("Refused to start the staged installer: {Detail}", untrusted.Detail);
            return new UpdateInstallResult(UpdateInstallOutcome.Untrusted, untrusted.Detail);
        }

        var start = new ProcessStartInfo(installerPath)
        {
            // No shell execute: this must be a direct CreateProcess so the arguments below are the
            // arguments Inno sees, with no shell association in between.
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory,
        };
        foreach (var argument in SilentArguments) start.ArgumentList.Add(argument);

        // A log beside the installer is the only forensics available once this process is gone.
        var log = Path.Combine(
            Path.GetDirectoryName(installerPath) ?? Path.GetTempPath(), "install.log");
        start.ArgumentList.Add($"/LOG={log}");

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return new UpdateInstallResult(UpdateInstallOutcome.Failed,
                    "The installer process did not start.");

            _logger.LogInformation(
                "Started the update installer (pid {Pid}); the shell will now close so files can be replaced.",
                process.Id);
            return new UpdateInstallResult(UpdateInstallOutcome.Started);
        }
        catch (Exception ex)
        {
            // Blocked by policy, AV quarantine between verification and launch, a missing file. The
            // app stays up and the banner keeps offering the release notes.
            _logger.LogWarning(ex, "The update installer could not be started.");
            return new UpdateInstallResult(UpdateInstallOutcome.Failed, ex.Message);
        }
    }
}
