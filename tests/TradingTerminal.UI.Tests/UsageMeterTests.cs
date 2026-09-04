using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The status bar: the token meter and the one-word state.
///
/// <para><b>The user is billed by their own provider for every turn, and the figure lived in a 9.5pt
/// grey string in the corner of a panel that is closed by default.</b> A number somebody pays for
/// cannot be somewhere they have to go and find it. It is a permanent readout in the status bar now,
/// beside the model that is spending it.</para>
///
/// <para>The meter is three proportional segments of one bar — fresh input, cached input, output —
/// and NOT a context-window gauge, because nothing in this application knows any model's window: it
/// talks to a dozen providers plus arbitrary custom model ids, so a limit invented here would be
/// wrong silently and stale permanently. These pin the arithmetic that keeps the three segments inside
/// their track, and the two states that must never be conflated: nothing spent yet, and a provider
/// that does not report what it spent.</para>
/// </summary>
[Collection(AuthoringCollection.Name)]
public sealed class UsageMeterTests : IDisposable
{
    private readonly string _sessionDir = Path.Combine(
        Path.GetTempPath(), "daxalgo-usage-" + Guid.NewGuid().ToString("N"));

    public UsageMeterTests() => AuthoringSessionStore.Directory = _sessionDir;

    public void Dispose()
    {
        AuthoringSessionStore.Directory = TestAuthoringRoot.Directory;
        try { System.IO.Directory.Delete(_sessionDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void An_untouched_session_reports_no_usage_at_all()
    {
        using var vm = Build();

        Assert.False(vm.HasUsage);
        // A zero nobody has spent yet is noise, not information — the readout is hidden, not zeroed.
        Assert.Equal(string.Empty, vm.UsageText);
        Assert.Equal(string.Empty, vm.UsageFallbackText);
        Assert.Equal("Idle", vm.StateText);
    }

    [Fact]
    public void A_provider_that_reports_nothing_says_so_rather_than_showing_zero()
    {
        using var vm = Build();
        vm.Messages.Add(new AuthoringMessage(CodegenRole.User, "build me something"));

        // An agent CLI typically reports no usage. Unknown is not the same as free, and a meter drawn
        // at zero would claim the turn cost nothing.
        Assert.False(vm.HasUsage);
        Assert.Equal("tokens: not reported", vm.UsageText);
        // The status bar's own words, so the view does not have to re-derive this in a trigger.
        Assert.Equal("tokens not reported", vm.UsageFallbackText);
    }

    [Fact]
    public void The_fallback_disappears_once_a_provider_actually_reports_usage()
    {
        using var vm = Build();
        vm.Messages.Add(new AuthoringMessage(CodegenRole.User, "build me something"));
        vm.InputTokens = 500;
        vm.OutputTokens = 120;

        // The meter is on screen now, so the words would be a duplicate of what the bar already says.
        Assert.True(vm.HasUsage);
        Assert.Equal(string.Empty, vm.UsageFallbackText);
    }

    [Fact]
    public void The_three_segments_split_the_track_and_never_overflow_it()
    {
        using var vm = Build();
        vm.Messages.Add(new AuthoringMessage(CodegenRole.User, "build me something"));
        vm.InputTokens = 8_000;
        vm.CachedTokens = 6_000;   // a SUBSET of input, not an addition to it
        vm.OutputTokens = 2_000;

        Assert.True(vm.HasUsage);
        Assert.Equal(10_000, vm.TotalTokens);

        // 2k fresh + 6k cached + 2k out of a 10k total, across the track's 84px interior.
        Assert.Equal(16.8, vm.MeterInputWidth, 2);
        Assert.Equal(50.4, vm.MeterCachedWidth, 2);
        Assert.Equal(16.8, vm.MeterOutputWidth, 2);

        // Counting the cached share as a third bucket would push the bar out of its own track.
        Assert.Equal(84, vm.MeterInputWidth + vm.MeterCachedWidth + vm.MeterOutputWidth, 1);
    }

    [Fact]
    public void An_uncached_session_puts_the_whole_prompt_in_the_full_price_segment()
    {
        using var vm = Build();
        vm.Messages.Add(new AuthoringMessage(CodegenRole.User, "build me something"));
        vm.InputTokens = 3_000;
        vm.OutputTokens = 1_000;

        Assert.Equal(0, vm.MeterCachedWidth);
        Assert.Equal(63, vm.MeterInputWidth, 2);
        Assert.Equal("4k", vm.TotalTokensText);
    }

    [Fact]
    public void The_estimate_is_marked_as_one_while_a_turn_is_streaming()
    {
        using var vm = Build();
        vm.Messages.Add(new AuthoringMessage(CodegenRole.User, "build me something"));
        vm.InputTokens = 1_000;
        vm.OutputTokens = 200;
        vm.IsUsageEstimated = true;

        // The "~" exists so nobody quotes a four-characters-per-token guess as a measurement.
        Assert.Contains("~200", vm.UsageText, StringComparison.Ordinal);
        Assert.Contains("~200", vm.UsageDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_state_word_and_the_dot_colour_come_from_one_expression()
    {
        using var vm = Build();
        vm.Messages.Add(new AuthoringMessage(CodegenRole.User, "build me something"));

        Assert.Equal("Ready", vm.StateText);
        Assert.Equal("Idle", vm.StateKind);

        // A turn that ended in a question is the one state nothing moves out of until the USER acts,
        // and the failure this exists to stop is somebody concluding the builder hung.
        vm.AwaitingAnswer = true;
        Assert.Equal("Waiting for you", vm.StateText);
        Assert.Equal("Ask", vm.StateKind);

        vm.AwaitingAnswer = false;
        vm.Diagnostics.Add(new StrategyDiagnostic(
            StrategyDiagnosticSeverity.Error, "CS0103", "The name 'x' does not exist", 4, 9, "Strategy.cs"));
        Assert.Equal("Build failed", vm.StateText);
        Assert.Equal("Error", vm.StateKind);
    }

    [Fact]
    public void A_warning_on_its_own_is_not_a_failed_build()
    {
        using var vm = Build();
        vm.Messages.Add(new AuthoringMessage(CodegenRole.User, "build me something"));
        vm.Diagnostics.Add(new StrategyDiagnostic(
            StrategyDiagnosticSeverity.Warning, "CS0168", "Variable is declared but never used", 7, 13, "Strategy.cs"));

        // A unit that compiled with a warning compiled.
        Assert.Equal("Ready", vm.StateText);
        Assert.Equal("Idle", vm.StateKind);
    }

    [Fact]
    public void A_file_chip_in_the_chat_opens_the_code_tab_not_the_preview()
    {
        // FocusFile sent every click to tab 0 while a stale doc comment claimed tab 0 was Code. It has
        // been Preview since the redesign, so clicking a file named in the transcript opened a picture
        // of the unit instead of the file it names.
        using var vm = Build();

        vm.WorkbenchTab = StrategyAuthoringViewModel.WorkbenchTabPreview;
        vm.FocusFileCommand.Execute(StrategyFile.DefaultName);

        Assert.Equal(StrategyAuthoringViewModel.WorkbenchTabCode, vm.WorkbenchTab);
        // A chip that opens a file in a closed panel does nothing the user can see.
        Assert.True(vm.IsWorkbenchOpen);
        Assert.Equal(StrategyFile.DefaultName, vm.SelectedFile!.Name);
    }

    private static StrategyAuthoringViewModel Build() => new(
        new RoslynStrategyCompiler(),
        new NullRegistry(),
        NullLogger<StrategyAuthoringViewModel>.Instance);

    private sealed class NullRegistry : IStrategyRegistry
    {
        public IReadOnlyList<StrategyCatalogEntry> All => [];

        public event EventHandler? Changed;

        public StrategyCatalogEntry? Find(string id) => null;

        public void Register(StrategyCatalogEntry entry) => Changed?.Invoke(this, EventArgs.Empty);

        public bool Remove(string id) => false;
    }
}
