using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;
using Xunit;
using PlanTask = TradingTerminal.Infrastructure.Strategies.Authoring.Swarm.BuildTask;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// A running swarm has to be VISIBLE, and this is the file that pins it.
///
/// <para><b>Reported from real use: "after the thinking, when it creates the plan, nothing happens — I
/// can't see the code anywhere and I can't see any of the tasks proceeding."</b> Two independent
/// causes, both the same shape of mistake: the pane was still wired to the pipeline the swarm
/// replaced.</para>
///
/// <list type="number">
///   <item>The status strip was seeded with the old six-step checklist and advanced by PREFIX-MATCHING
///     the activity strings the single conversation used to emit ("Asking…", "Compiling…"). The swarm
///     emits nothing of the kind, so not one prefix ever matched: the strip sat on step 1 of 6 for the
///     whole run, and a build that was working looked exactly like one that had hung.</item>
///   <item>Files reached the editor only when the whole turn returned. Through a multi-minute fan-out
///     the Code tab stayed empty — and an empty editor is indistinguishable from one that is never
///     going to fill.</item>
/// </list>
/// </summary>
[Collection(AuthoringCollection.Name)]
public sealed class SwarmProgressReachesThePaneTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "daxalgo-progress-" + Guid.NewGuid().ToString("N"));

    public SwarmProgressReachesThePaneTests() => AuthoringSessionStore.Directory = _dir;

    public void Dispose()
    {
        AuthoringSessionStore.Directory = TestAuthoringRoot.Directory;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static StrategyAuthoringViewModel Pane() => new(
        new RoslynStrategyCompiler(),
        new NullRegistry(),
        NullLogger<StrategyAuthoringViewModel>.Instance);

    /// <summary>A registry that accepts everything and remembers nothing — these tests are about the
    /// pane's own progress reporting, not about registration.</summary>
    private sealed class NullRegistry : IStrategyRegistry
    {
        public IReadOnlyList<StrategyCatalogEntry> All => [];

        public event EventHandler? Changed;

        public StrategyCatalogEntry? Find(string id) => null;

        public void Register(StrategyCatalogEntry entry) => Changed?.Invoke(this, EventArgs.Empty);

        public bool Remove(string id) => false;
    }

    private static PlanTask Task(string id, string title, string file) =>
        new(id, title, TaskKind.Panel, file, "do the thing", []);

    private static BuildPlan Plan(params PlanTask[] tasks) =>
        new(UnitContract.Minimal("U", AuthoringKind.Strategy),
            [new Milestone("m1", "Build", tasks)],
            [],
            []);

    private static SwarmEvent.Planned Planned(BuildPlan plan) => new(plan, PlanOrigin.Planned);

    /// <summary>Drives the pane's swarm handler the way a run does.</summary>
    private static void Feed(StrategyAuthoringViewModel pane, params SwarmEvent[] events)
    {
        var handler = typeof(StrategyAuthoringViewModel)
            .GetMethod("OnSwarmEvent", BindingFlags.NonPublic | BindingFlags.Instance)!;

        foreach (var evt in events) handler.Invoke(pane, [evt]);
    }

    [Fact]
    public void The_step_counter_counts_the_plans_own_work()
    {
        // It used to count a pipeline that no longer runs, and sat on its first step forever.
        var pane = Pane();

        Feed(pane, Planned(Plan(
            Task("t1", "Rolling imbalance", "Imbalance.cs"),
            Task("t2", "Depth ladder", "Ladder.cs"))));

        Assert.Contains(pane.Tasks, t => t.Title == "Rolling imbalance");
        Assert.Contains(pane.Tasks, t => t.Title == "Depth ladder");
        Assert.DoesNotContain(pane.Tasks, t => t.Title == "Load skills");
    }

    [Fact]
    public void A_task_starting_moves_the_verb_and_the_step()
    {
        var pane = Pane();
        var first = Task("t1", "Rolling imbalance", "Imbalance.cs");

        Feed(pane,
            Planned(Plan(first, Task("t2", "Depth ladder", "Ladder.cs"))),
            new SwarmEvent.TaskStarted(first, IsRepair: false));

        Assert.Equal("Rolling imbalance…", pane.WorkingVerb);
        Assert.Equal("step 1 of 3", pane.StepText);
    }

    [Fact]
    public void The_second_task_advances_the_counter_rather_than_repeating_the_first()
    {
        var pane = Pane();
        var first = Task("t1", "Rolling imbalance", "Imbalance.cs");
        var second = Task("t2", "Depth ladder", "Ladder.cs");

        Feed(pane,
            Planned(Plan(first, second)),
            new SwarmEvent.TaskStarted(first, IsRepair: false),
            new SwarmEvent.TaskFinished(first, Wrote: true, CodegenUsage.None, null, []),
            new SwarmEvent.TaskStarted(second, IsRepair: false));

        Assert.Equal("Depth ladder…", pane.WorkingVerb);
        Assert.Equal("step 2 of 3", pane.StepText);
    }

    [Fact]
    public void The_code_appears_as_each_task_writes_it()
    {
        // Not when the whole turn returns. Through a multi-minute fan-out an empty Code tab is
        // indistinguishable from one that is never going to fill.
        var pane = Pane();
        var first = Task("t1", "Rolling imbalance", "Imbalance.cs");

        Feed(pane,
            Planned(Plan(first)),
            new SwarmEvent.TaskFinished(
                first, Wrote: true, CodegenUsage.None, null,
                [new StrategyFile("Imbalance.cs", "public sealed class Imbalance { }")]));

        Assert.Contains(pane.Files, f => f.Name == "Imbalance.cs");
        Assert.Contains("Imbalance", pane.Files[0].Content);
    }

    [Fact]
    public void A_live_update_does_not_yank_the_file_the_user_is_reading()
    {
        // The swarm calls this once per task. Resetting the selection to the first file would pull the
        // editor out from under somebody several times a minute, on the exact file they chose.
        var pane = Pane();
        var first = Task("t1", "Maths", "Imbalance.cs");
        var second = Task("t2", "Panel", "Ladder.cs");

        Feed(pane,
            Planned(Plan(first, second)),
            new SwarmEvent.TaskFinished(first, true, CodegenUsage.None, null,
            [
                new StrategyFile("Imbalance.cs", "a"),
                new StrategyFile("Ladder.cs", "b"),
            ]));

        pane.SelectedFile = pane.Files.First(f => f.Name == "Ladder.cs");

        Feed(pane, new SwarmEvent.TaskFinished(second, true, CodegenUsage.None, null,
        [
            new StrategyFile("Imbalance.cs", "a"),
            new StrategyFile("Ladder.cs", "b - now with the panel"),
        ]));

        Assert.Equal("Ladder.cs", pane.SelectedFile?.Name);
        Assert.Contains("now with the panel", pane.SelectedFile!.Content);
    }

    [Fact]
    public void A_task_that_produced_nothing_is_shown_as_such_rather_than_as_done()
    {
        var pane = Pane();
        var only = Task("t1", "Maths", "Imbalance.cs");

        Feed(pane,
            Planned(Plan(only)),
            new SwarmEvent.TaskFinished(only, Wrote: false, CodegenUsage.None, "returned no file", []));

        var row = Assert.Single(pane.Board);
        Assert.Equal(SwarmTaskState.Empty, row.State);

        // The editor keeps whatever it had — the pane opens on a scaffold, and a task that produced
        // nothing must not blank it. "Wrote nothing" is a state to SHOW, not one to act on.
        Assert.DoesNotContain(pane.Files, f => f.Name == "Imbalance.cs");
    }
}
