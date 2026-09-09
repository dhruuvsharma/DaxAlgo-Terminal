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
    public void ATaskThatProducedNothingIsNotAFinishedRow()
    {
        // It is a turn the user paid for. A board that showed it as done would hide the one thing
        // worth noticing about it.
        var row = new SwarmTaskRow(Task()) { State = SwarmTaskState.Empty };

        Assert.Equal("∅", row.Glyph);
        Assert.NotEqual("✓", row.Glyph);
    }
}
