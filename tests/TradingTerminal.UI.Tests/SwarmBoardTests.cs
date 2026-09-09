using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using PlanTask = TradingTerminal.Infrastructure.Strategies.Authoring.Swarm.BuildTask;
using TaskKind = TradingTerminal.Infrastructure.Strategies.Authoring.Swarm.TaskKind;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The task board — one row per planned task, turning as each starts.
///
/// <para><b>This is the answer to the failure that killed the committee.</b> A fan-out takes minutes
/// and, without a board, reports nothing a user can read: five saved sessions on a real machine show
/// somebody watching a status line through a run that produced no code and concluding the builder did
/// not work. So the properties worth pinning are that a row exists per task, that it turns when the
/// task STARTS rather than when it finishes, and that a task which produced nothing says so rather
/// than looking like every other finished row.</para>
/// </summary>
public sealed class SwarmTaskRowTests
{
    private static PlanTask Task(string id = "t1", TaskKind kind = TaskKind.Panel) =>
        new(id, "Depth ladder", kind, "Ladder.cs", "paint the ladder", []);

    [Fact]
    public void ARowStartsWaitingAndCarriesTheMergeRuleOnItsFace()
    {
        var row = new SwarmTaskRow(Task());

        Assert.Equal(SwarmTaskState.Waiting, row.State);
        Assert.Equal("Ladder.cs", row.File);
        Assert.Equal("Panel", row.Kind);
        Assert.Equal("·", row.Glyph);
    }

    [Fact]
    public void TheGlyphFollowsTheStateSoOneBindingIsEnough()
    {
        var row = new SwarmTaskRow(Task());
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.State = SwarmTaskState.Running;
        Assert.Equal("▶", row.Glyph);

        row.State = SwarmTaskState.Repairing;
        Assert.Equal("↻", row.Glyph);

        row.State = SwarmTaskState.Done;
        Assert.Equal("✓", row.Glyph);

        // The glyph is derived, so it has to announce itself or a bound row would never repaint.
        Assert.Contains(nameof(SwarmTaskRow.Glyph), changed);
    }

    [Fact]
    public void ARunningRowShowsSomethingThatMovesBeforeAnyTokensAreReported()
    {
        // THE BUG A REAL RUN SHOWED: six tasks, every one reading "0 tok", nothing moving, and no way
        // to tell a model thinking hard from a provider that had stopped answering. Many providers
        // report usage only when a call finishes, so tokens are the WRONG thing to wait on.
        var row = new SwarmTaskRow(Task()) { State = SwarmTaskState.Running };

        row.Tick(DateTime.UtcNow.AddSeconds(12));

        Assert.Contains("12s", row.Vitals);
    }

    [Fact]
    public void ReasoningCharactersCountAsProofOfLife()
    {
        var row = new SwarmTaskRow(Task()) { State = SwarmTaskState.Running, Thinking = 4_210 };

        row.Tick(DateTime.UtcNow.AddSeconds(5));

        Assert.Contains("4,210 thinking", row.Vitals);
        Assert.False(row.Silent, "it has said plenty");
    }

    [Fact]
    public void A_row_that_has_received_nothing_at_all_says_so()
    {
        // Not a diagnosis — a reasoning model can legitimately spend minutes before its first byte, and
        // 278 seconds has been measured on one. It is the difference between a row that is quiet and a
        // row that has never spoken, which is the user's call to make rather than ours.
        var row = new SwarmTaskRow(Task()) { State = SwarmTaskState.Running };

        row.Tick(DateTime.UtcNow.Add(SwarmTaskRow.SilenceBeforeDoubt).AddSeconds(5));

        Assert.True(row.Silent);
        Assert.Contains("nothing received yet", row.Vitals);
    }

    [Fact]
    public void A_row_that_is_merely_slow_is_not_called_silent()
    {
        var row = new SwarmTaskRow(Task()) { State = SwarmTaskState.Running, Written = 12 };

        row.Tick(DateTime.UtcNow.Add(SwarmTaskRow.SilenceBeforeDoubt).AddMinutes(5));

        Assert.False(row.Silent);
        Assert.DoesNotContain("nothing received", row.Vitals);
    }

    [Fact]
    public void AWaitingRowShowsNothingAtAll()
    {
        // Five queued rows each showing a clock would read as five things running.
        var row = new SwarmTaskRow(Task());

        row.Tick(DateTime.UtcNow.AddMinutes(3));

        Assert.Equal(string.Empty, row.Vitals);
    }

    [Fact]
    public void ATaskThatProducedNothingIsNotAFinishedRow()
    {
        // It is a turn the user paid for. A board that showed it as done would hide the one thing
        // worth noticing about it.
        var row = new SwarmTaskRow(Task()) { State = SwarmTaskState.Empty };

        Assert.Equal("∅", row.Glyph);
        Assert.NotEqual("✓", row.Glyph);
    }
}
