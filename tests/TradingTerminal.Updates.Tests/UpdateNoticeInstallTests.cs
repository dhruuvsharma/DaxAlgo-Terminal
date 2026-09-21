using FluentAssertions;
using TradingTerminal.Core.Updates;
using TradingTerminal.UI.Updates;
using Xunit;

namespace TradingTerminal.Updates.Tests;

/// <summary>
/// The accept path: what happens between the user pressing "Update now" and the shell closing.
///
/// <para>The rule under nearly every test here is the same one — <b>the app only closes when an
/// installer is genuinely running</b>. Closing after a refused or failed download would throw away
/// the user's session and leave them on the same version, which is the worst of both outcomes.</para>
/// </summary>
public sealed class UpdateNoticeInstallTests : IDisposable
{
    private readonly string _storePath = Path.Combine(
        Path.GetTempPath(), "daxalgo-install-vm-tests", Guid.NewGuid().ToString("N"), "dismissed.json");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_storePath)!, recursive: true); } catch { /* temp */ }
    }

    private DismissedUpdateStore NewStore() => new(_storePath);

    private static UpdateCheckResult Installable(
        string? downloadUrl = "https://releases.example.com/setup.exe",
        string? sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef") =>
        new(UpdateOutcome.UpdateAvailable, new Version(1, 3, 2),
            new UpdateManifest
            {
                Version = "1.4.0",
                ReleaseNotesUrl = "https://example.com/notes",
                DownloadUrl = downloadUrl ?? string.Empty,
                Sha256 = sha256 ?? string.Empty,
            });

    private UpdateNoticeViewModel Vm(
        FakeDownloader? downloader = null,
        FakeInstaller? installer = null,
        Action? requestExit = null,
        UpdateCheckResult? latest = null) =>
        new(new FakeNotifier { Latest = latest ?? Installable() }, NewStore(),
            downloader, installer, requestExit);

    [Fact]
    public void Offers_no_install_button_when_nothing_was_injected_to_install_with()
    {
        // The historical shape: a shell with only a notifier still gets the link-only strip.
        var vm = Vm();

        vm.IsVisible.Should().BeTrue();
        vm.CanInstall.Should().BeFalse();
        vm.InstallCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Offers_to_install_when_both_seams_are_live_and_the_release_ships_an_installer()
    {
        var vm = Vm(new FakeDownloader(), new FakeInstaller());

        vm.CanInstall.Should().BeTrue();
        vm.InstallCommand.CanExecute(null).Should().BeTrue();
    }

    [Theory]
    [InlineData(null, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]  // no download URL
    [InlineData("https://releases.example.com/setup.exe", null)]                            // nothing to check it against
    public void Offers_no_install_for_an_announce_only_release(string? url, string? sha256)
    {
        var vm = Vm(new FakeDownloader(), new FakeInstaller(),
            latest: Installable(downloadUrl: url, sha256: sha256));

        vm.IsVisible.Should().BeTrue("the release is still worth announcing");
        vm.CanInstall.Should().BeFalse();
    }

    [Fact]
    public void Offers_no_install_when_the_build_is_not_allowed_to_install()
    {
        var vm = Vm(new FakeDownloader { CanInstall = false }, new FakeInstaller { CanInstall = false });

        vm.CanInstall.Should().BeFalse();
    }

    [Fact]
    public async Task Accepting_downloads_launches_the_installer_and_asks_the_shell_to_close()
    {
        var downloader = new FakeDownloader();
        var installer = new FakeInstaller();
        var exits = 0;
        var vm = Vm(downloader, installer, requestExit: () => exits++);

        await vm.InstallCommand.ExecuteAsync(null);

        downloader.Calls.Should().Be(1);
        installer.Launched.Should().Be(downloader.StagedPath);
        exits.Should().Be(1, "the files being replaced are the ones this process is running from");
    }

    [Fact]
    public async Task Raises_ExitRequested_as_well_as_the_injected_callback()
    {
        var raised = 0;
        var vm = Vm(new FakeDownloader(), new FakeInstaller());
        vm.ExitRequested += () => raised++;

        await vm.InstallCommand.ExecuteAsync(null);

        raised.Should().Be(1);
    }

    [Theory]
    [InlineData(UpdateDownloadOutcome.HashMismatch)]
    [InlineData(UpdateDownloadOutcome.UntrustedInstaller)]
    [InlineData(UpdateDownloadOutcome.Failed)]
    [InlineData(UpdateDownloadOutcome.NotOffered)]
    [InlineData(UpdateDownloadOutcome.NotAllowed)]
    public async Task A_refused_download_never_reaches_the_installer_and_leaves_the_app_running(
        UpdateDownloadOutcome outcome)
    {
        var installer = new FakeInstaller();
        var exits = 0;
        var vm = Vm(new FakeDownloader { Outcome = outcome }, installer, requestExit: () => exits++);

        await vm.InstallCommand.ExecuteAsync(null);

        installer.Launched.Should().BeNull("nothing unverified may be executed");
        exits.Should().Be(0);
        vm.IsInstalling.Should().BeFalse();
        vm.StatusText.Should().NotBeEmpty("the user needs to know the update did not happen");
        vm.IsVisible.Should().BeTrue("the release-notes fallback is still the way out");
    }

    [Fact]
    public async Task An_installer_that_will_not_start_leaves_the_app_running()
    {
        var exits = 0;
        var vm = Vm(new FakeDownloader(), new FakeInstaller { Outcome = UpdateInstallOutcome.Failed },
            requestExit: () => exits++);

        await vm.InstallCommand.ExecuteAsync(null);

        exits.Should().Be(0, "closing the app when nothing is installing only loses the user's session");
        vm.IsInstalling.Should().BeFalse();
        vm.StatusText.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_downloader_that_throws_does_not_take_the_shell_with_it()
    {
        var vm = Vm(new FakeDownloader { Throw = true }, new FakeInstaller());

        var install = async () => await vm.InstallCommand.ExecuteAsync(null);

        await install.Should().NotThrowAsync();
        vm.IsInstalling.Should().BeFalse();
        vm.StatusText.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Reports_download_progress_as_a_percentage()
    {
        var vm = Vm(new FakeDownloader { ReportBytes = [(25, 100), (50, 100)] }, new FakeInstaller());

        await vm.InstallCommand.ExecuteAsync(null);

        vm.ProgressPercent.Should().Be(50);
    }

    [Fact]
    public async Task Falls_back_to_a_marquee_when_the_release_host_sends_no_length()
    {
        var vm = Vm(new FakeDownloader { ReportBytes = [(50, 100), (4096, null)] }, new FakeInstaller());

        await vm.InstallCommand.ExecuteAsync(null);

        vm.IsProgressIndeterminate.Should().BeTrue();
    }

    [Fact]
    public async Task Dismissing_mid_download_cancels_it_rather_than_letting_it_finish_unseen()
    {
        var downloader = new FakeDownloader { BlockUntilCancelled = true };
        var vm = Vm(downloader, new FakeInstaller());

        var install = vm.InstallCommand.ExecuteAsync(null);
        await downloader.Started.Task;
        vm.DismissCommand.Execute(null);
        await install;

        vm.IsVisible.Should().BeFalse();
        downloader.WasCancelled.Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_returns_the_strip_to_offering_the_update_again()
    {
        var downloader = new FakeDownloader { BlockUntilCancelled = true };
        var vm = Vm(downloader, new FakeInstaller());

        var install = vm.InstallCommand.ExecuteAsync(null);
        await downloader.Started.Task;
        vm.CancelInstallCommand.Execute(null);
        await install;

        vm.IsInstalling.Should().BeFalse();
        vm.IsVisible.Should().BeTrue();
        vm.InstallCommand.CanExecute(null).Should().BeTrue("the user may change their mind");
    }

    [Fact]
    public async Task Detach_cancels_a_download_still_in_flight()
    {
        var downloader = new FakeDownloader { BlockUntilCancelled = true };
        var vm = Vm(downloader, new FakeInstaller());

        var install = vm.InstallCommand.ExecuteAsync(null);
        await downloader.Started.Task;
        vm.Detach();
        await install;

        downloader.WasCancelled.Should().BeTrue("a discarded view-model must not keep downloading");
    }

    // ── doubles ─────────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeDownloader : IUpdateDownloader
    {
        public bool CanInstall { get; init; } = true;
        public UpdateDownloadOutcome Outcome { get; init; } = UpdateDownloadOutcome.Ok;
        public bool Throw { get; init; }
        public bool BlockUntilCancelled { get; init; }
        public (long Received, long? Total)[] ReportBytes { get; init; } = [];
        public string StagedPath { get; } = Path.Combine(Path.GetTempPath(), "staged-setup.exe");
        public int Calls { get; private set; }
        public bool WasCancelled { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UpdateDownloadResult> DownloadAsync(
            UpdateManifest manifest,
            IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Throw)
            {
                Started.TrySetResult();
                throw new InvalidOperationException("the seam misbehaved");
            }

            foreach (var (received, total) in ReportBytes)
                progress?.Report(new UpdateDownloadProgress(received, total));

            Started.TrySetResult();

            if (BlockUntilCancelled)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    WasCancelled = true;
                    return new UpdateDownloadResult(UpdateDownloadOutcome.Cancelled);
                }
            }

            return Outcome == UpdateDownloadOutcome.Ok
                ? new UpdateDownloadResult(UpdateDownloadOutcome.Ok, StagedPath)
                : new UpdateDownloadResult(Outcome, Detail: "refused by the fake");
        }
    }

    private sealed class FakeInstaller : IUpdateInstaller
    {
        public bool CanInstall { get; init; } = true;
        public UpdateInstallOutcome Outcome { get; init; } = UpdateInstallOutcome.Started;
        public string? Launched { get; private set; }

        public UpdateInstallResult Launch(string installerPath)
        {
            if (Outcome != UpdateInstallOutcome.Started) return new UpdateInstallResult(Outcome, "refused");
            Launched = installerPath;
            return new UpdateInstallResult(UpdateInstallOutcome.Started);
        }
    }

    private sealed class FakeNotifier : IUpdateNotifier
    {
        public event Action<UpdateCheckResult>? UpdateAvailable;
        public UpdateCheckResult? Latest { get; set; }
        public void Raise(UpdateCheckResult result) => UpdateAvailable?.Invoke(result);
    }
}
