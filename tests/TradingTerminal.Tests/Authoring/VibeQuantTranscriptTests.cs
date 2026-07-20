using System.Linq;
using FluentAssertions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Tests.Authoring;

/// <summary>
/// The pure logic under the Vibe Quant agent workspace (issue #29): the line diff that feeds the
/// per-turn file chips and the review gate, the chip pack/unpack that survives the session snapshot,
/// and the transcript message kinds the duck-typed XAML templates trigger on — those string values
/// ARE the contract with VibeQuantStyles.xaml, so they are pinned here.
/// </summary>
public sealed class VibeQuantTranscriptTests
{
    // ── LineDiff ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_brand_new_file_counts_as_all_added()
    {
        var (added, removed) = LineDiff.Count(string.Empty, "a\nb\nc");

        added.Should().Be(3);
        removed.Should().Be(0);
    }

    [Fact]
    public void An_edit_counts_only_the_changed_lines()
    {
        var before = "one\ntwo\nthree\nfour";
        var after = "one\nTWO\nthree\nfour\nfive";

        var (added, removed) = LineDiff.Count(before, after);

        added.Should().Be(2, "'TWO' replaced a line and 'five' is new");
        removed.Should().Be(1, "'two' went away");
    }

    [Fact]
    public void Identical_content_diffs_to_context_only()
    {
        var lines = LineDiff.Build("a\nb", "a\nb");

        lines.Should().OnlyContain(l => l.Kind == "ctx");
    }

    [Fact]
    public void Windows_and_unix_line_endings_compare_equal()
    {
        var (added, removed) = LineDiff.Count("a\r\nb", "a\nb");

        added.Should().Be(0);
        removed.Should().Be(0);
    }

    [Fact]
    public void Deletions_come_before_additions_inside_a_hunk()
    {
        var lines = LineDiff.Build("keep\nold", "keep\nnew");

        lines.Select(l => l.Kind).Should().ContainInOrder("ctx", "del", "add");
    }

    // ── FileChangeSummary (chip persistence) ────────────────────────────────────────────────────────

    [Fact]
    public void Chips_survive_a_pack_unpack_round_trip()
    {
        var packed = FileChangeSummary.Pack(
            [new FileChangeSummary("MyStrategy.cs", 112, 0), new FileChangeSummary("Helpers.cs", 4, 8)]);

        var restored = FileChangeSummary.Unpack(packed);

        restored.Should().BeEquivalentTo(
            [new FileChangeSummary("MyStrategy.cs", 112, 0), new FileChangeSummary("Helpers.cs", 4, 8)]);
    }

    [Fact]
    public void Garbage_in_a_snapshot_unpacks_to_null_not_a_throw()
    {
        FileChangeSummary.Unpack("not|a").Should().BeNull();
        FileChangeSummary.Unpack("   ").Should().BeNull();
        FileChangeSummary.Unpack(null).Should().BeNull();
    }

    [Fact]
    public void The_counts_label_omits_a_zero_removal()
    {
        new FileChangeSummary("a.cs", 5, 0).Counts.Should().Be("+5");
        new FileChangeSummary("a.cs", 5, 2).Counts.Should().Be("+5 −2");
    }

    // ── AuthoringMessage kinds (the duck-typed template contract) ───────────────────────────────────

    [Fact]
    public void Message_kinds_match_the_strings_the_shared_templates_trigger_on()
    {
        new AuthoringMessage(CodegenRole.User, "hi").Kind.Should().Be("User");
        new AuthoringMessage(CodegenRole.Assistant, "hello").Kind.Should().Be("Assistant");
        AuthoringMessage.System("note").Kind.Should().Be("Note");
        AuthoringMessage.Tool("Ok", "Compiled", "2 files").Kind.Should().Be("Tool");
        AuthoringMessage.PlanText("✓ done").Kind.Should().Be("PlanText");
        AuthoringMessage.FilesChanged([new FileChangeSummary("a.cs", 1, 0)]).Kind.Should().Be("Files");
    }

    [Fact]
    public void A_tool_card_without_full_output_hides_its_expander()
    {
        AuthoringMessage.Tool("Ok", "Compiled", "detail").HasMore.Should().BeFalse();
        AuthoringMessage.Tool("Fail", "Compile failed", "detail", "full output").HasMore.Should().BeTrue();
    }

    [Fact]
    public void A_plan_snapshot_freezes_task_states_into_glyph_lines()
    {
        var done = new BuildTask("Generate") { State = BuildTaskState.Done };
        var failed = new BuildTask("Compile") { State = BuildTaskState.Failed };
        var pending = new BuildTask("Backtest smoke");

        var text = AuthoringMessage.Plan([done, failed, pending]).PlanSnapshotText();

        text.Should().Be("✓ Generate\n✕ Compile\n○ Backtest smoke");
    }

    // ── ReviewFileEntry (the review gate's per-file strip) ──────────────────────────────────────────

    [Fact]
    public void A_review_entry_derives_its_counts_from_the_diff()
    {
        var entry = new ReviewFileEntry("a.cs", LineDiff.Build("x\ny", "x\nz\nw"));

        entry.Added.Should().Be(2);
        entry.Removed.Should().Be(1);
        entry.Counts.Should().Be("+2 −1");
    }
}
