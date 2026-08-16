using FluentAssertions;
using TradingTerminal.Core.Updates;
using TradingTerminal.UI.Updates;
using Xunit;

namespace TradingTerminal.Updates.Tests;

/// <summary>
/// The banner's rules: show only for a real, newer, non-dismissed release; never offer a plaintext
/// link; and stop nagging once dismissed, across restarts.
/// </summary>
public sealed class UpdateNoticeViewModelTests : IDisposable
{
    private readonly string _storePath = Path.Combine(
        Path.GetTempPath(), "daxalgo-update-tests", Guid.NewGuid().ToString("N"), "dismissed.json");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_storePath)!, recursive: true); } catch { /* temp */ }
    }

    private DismissedUpdateStore NewStore() => new(_storePath);

    private static UpdateCheckResult Available(string version = "1.4.0", string notes = "https://example.com/notes") =>
        new(UpdateOutcome.UpdateAvailable, new Version(1, 3, 2),
            new UpdateManifest { Version = version, ReleaseNotesUrl = notes });

    [Fact]
    public void Stays_hidden_with_no_notifier()
    {
        var vm = new UpdateNoticeViewModel(notifier: null, NewStore());

        vm.IsVisible.Should().BeFalse();
    }

    [Fact]
    public void Stays_hidden_while_the_build_is_current()
    {
        var notifier = new FakeNotifier();
        var vm = new UpdateNoticeViewModel(notifier, NewStore());

        notifier.Raise(new UpdateCheckResult(UpdateOutcome.UpToDate, new Version(1, 3, 2)));

        vm.IsVisible.Should().BeFalse();
    }

    [Fact]
    public void Shows_the_running_and_published_versions_when_an_update_arrives()
    {
        var notifier = new FakeNotifier();
        var vm = new UpdateNoticeViewModel(notifier, NewStore());

        notifier.Raise(Available());

        vm.IsVisible.Should().BeTrue();
        vm.AvailableVersion.Should().Be("1.4.0");
        vm.Message.Should().Contain("1.4.0").And.Contain("1.3.2");
        vm.HasReleaseNotes.Should().BeTrue();
    }

    [Fact]
    public void Catches_up_on_a_check_that_completed_before_the_window_opened()
    {
        // The service checks 20s after start-up; a window opened later must not miss the event.
        var notifier = new FakeNotifier { Latest = Available() };

        var vm = new UpdateNoticeViewModel(notifier, NewStore());

        vm.IsVisible.Should().BeTrue();
        vm.AvailableVersion.Should().Be("1.4.0");
    }

    [Theory]
    [InlineData("http://example.com/notes")]  // plaintext — hijackable
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("")]
    [InlineData("not a url")]
    public void Refuses_to_offer_a_link_that_is_not_https(string notes)
    {
        var notifier = new FakeNotifier();
        var vm = new UpdateNoticeViewModel(notifier, NewStore());

        notifier.Raise(Available(notes: notes));

        vm.IsVisible.Should().BeTrue("the version notice is still useful without a link");
        vm.HasReleaseNotes.Should().BeFalse();
    }

    [Fact]
    public void Dismissing_hides_the_strip_and_suppresses_that_version_on_the_next_launch()
    {
        var notifier = new FakeNotifier();
        var vm = new UpdateNoticeViewModel(notifier, NewStore());
        notifier.Raise(Available());

        vm.DismissCommand.Execute(null);
        vm.IsVisible.Should().BeFalse();

        // A fresh store instance reads the file back, standing in for a restart.
        var afterRestart = new UpdateNoticeViewModel(new FakeNotifier { Latest = Available() }, NewStore());
        afterRestart.IsVisible.Should().BeFalse();
    }

    [Fact]
    public void Dismissing_one_version_does_not_suppress_the_next_release()
    {
        var store = NewStore();
        var first = new UpdateNoticeViewModel(new FakeNotifier { Latest = Available("1.4.0") }, store);
        first.DismissCommand.Execute(null);

        var next = new UpdateNoticeViewModel(new FakeNotifier { Latest = Available("1.5.0") }, NewStore());

        next.IsVisible.Should().BeTrue();
        next.AvailableVersion.Should().Be("1.5.0");
    }

    [Fact]
    public void Detach_unsubscribes_so_the_singleton_notifier_cannot_pin_a_dead_view_model()
    {
        var notifier = new FakeNotifier();
        var vm = new UpdateNoticeViewModel(notifier, NewStore());

        vm.Detach();
        notifier.Raise(Available());

        notifier.SubscriberCount.Should().Be(0);
        vm.IsVisible.Should().BeFalse();
    }

    private sealed class FakeNotifier : IUpdateNotifier
    {
        public event Action<UpdateCheckResult>? UpdateAvailable;
        public UpdateCheckResult? Latest { get; set; }
        public int SubscriberCount => UpdateAvailable?.GetInvocationList().Length ?? 0;
        public void Raise(UpdateCheckResult result) => UpdateAvailable?.Invoke(result);
    }
}
