using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Core.Updates;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Updates;
using Xunit;

namespace TradingTerminal.Updates.Tests;

/// <summary>
/// The last gate before an executable runs as the user. Nothing here starts a real installer: the
/// property worth pinning is that every doubtful path REFUSES, and that the flags handed to Inno
/// still match what the .iss is written to expect.
/// </summary>
public sealed class SilentSetupUpdateInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "daxalgo-install-tests", Guid.NewGuid().ToString("N"));

    public SilentSetupUpdateInstallerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private string StagedFile(string name = "DaxAlgo-Terminal-Setup-v1.4.0.exe")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "pretend installer");
        return path;
    }

    private static SilentSetupUpdateInstaller Installer(
        IPluginSignatureInspector? signatures = null,
        bool allowed = true,
        string? pinnedThumbprint = null) =>
        new(signatures ?? FakeInspector.Trusted, allowed, pinnedThumbprint,
            NullLogger<SilentSetupUpdateInstaller>.Instance);

    [Fact]
    public void Refuses_when_installing_is_switched_off()
    {
        var result = Installer(allowed: false).Launch(StagedFile());

        result.Outcome.Should().Be(UpdateInstallOutcome.NotAllowed);
        result.Started.Should().BeFalse();
    }

    [Fact]
    public void Refuses_an_unsigned_file_even_though_the_downloader_already_checked_it()
    {
        // The re-check closes the window between verification and execution. If it ever stops
        // refusing, that window is open again.
        var result = Installer(FakeInspector.Unsigned).Launch(StagedFile());

        result.Outcome.Should().Be(UpdateInstallOutcome.Untrusted);
    }

    [Fact]
    public void Refuses_a_file_whose_signature_no_longer_verifies()
    {
        var result = Installer(FakeInspector.Tampered).Launch(StagedFile());

        result.Outcome.Should().Be(UpdateInstallOutcome.Untrusted);
    }

    [Fact]
    public void Refuses_a_file_signed_by_someone_other_than_the_pinned_certificate()
    {
        var result = Installer(FakeInspector.Trusted, pinnedThumbprint: "00112233").Launch(StagedFile());

        result.Outcome.Should().Be(UpdateInstallOutcome.Untrusted);
    }

    [Fact]
    public void Refuses_a_staged_file_that_has_vanished()
    {
        var result = Installer().Launch(Path.Combine(_dir, "never-downloaded.exe"));

        result.Outcome.Should().Be(UpdateInstallOutcome.Untrusted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Refuses_an_empty_path(string path)
    {
        var result = Installer().Launch(path);

        result.Outcome.Should().Be(UpdateInstallOutcome.Untrusted);
    }

    [Fact]
    public void Reports_failure_rather_than_throwing_when_the_process_cannot_start()
    {
        // A "trusted" file that Windows will not execute — the shell must stay up and keep offering
        // the release-notes link rather than fault.
        var notAnExecutable = Path.Combine(_dir, "setup.notanexe");
        File.WriteAllText(notAnExecutable, "not a PE file");

        var result = Installer().Launch(notAnExecutable);

        result.Outcome.Should().Be(UpdateInstallOutcome.Failed);
    }

    [Fact]
    public void Asks_Inno_for_a_silent_install_that_does_not_reboot_and_does_relaunch()
    {
        // These four flags are the contract with installer/DaxAlgoTerminal.iss. Drop /DAXRELAUNCH=1
        // and a silent upgrade leaves the user staring at a closed app; drop /NORESTART and the
        // installer may reboot a machine with live positions on it.
        SilentSetupUpdateInstaller.SilentArguments.Should().Equal(
            "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/DAXRELAUNCH=1");
    }

    private sealed class FakeInspector(PluginSignature signature) : IPluginSignatureInspector
    {
        public static FakeInspector Trusted { get; } = new(new PluginSignature(true, true, "ABCDEF", "CN=DaxAlgo"));
        public static FakeInspector Unsigned { get; } = new(PluginSignature.Unsigned);
        public static FakeInspector Tampered { get; } = new(new PluginSignature(true, false, "ABCDEF", "CN=DaxAlgo"));

        public PluginSignature Inspect(string assemblyPath) => signature;
    }
}
