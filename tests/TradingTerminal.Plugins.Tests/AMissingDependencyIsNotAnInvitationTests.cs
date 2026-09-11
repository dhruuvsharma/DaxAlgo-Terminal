using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// A builder whose dependency has not been written yet must be told so, not left to guess.
///
/// <para><b>Measured, and it produced the worst kind of failure: a plausible one.</b> An eight-task
/// plan had the geometry helper stall; the kernel was then built while <c>FootprintGeometry.cs</c> did
/// not exist. <see cref="SwarmContext.ComposeBuild"/> skipped the absent dependency in silence, so the
/// kernel builder saw the type named in the contract, no file defining it, and an instruction to write
/// a complete file — and did the reasonable thing. It declared its own copy.</para>
///
/// <para>The repair round then wrote the real <c>FootprintGeometry.cs</c>, exactly as it should, and the
/// unit ended with one type declared in two files and fifteen <c>CS0229: Ambiguity between
/// FootprintGeometry.BodyWidth and FootprintGeometry.BodyWidth</c> errors. A missing file became a
/// duplicated type, which is harder to repair than what it came from: neither owner is wrong on its
/// own, and the diagnostic names a file that is not at fault.</para>
///
/// <para>The one-file-one-owner rule stopped two tasks writing one FILE. Nothing stopped two files
/// declaring one TYPE.</para>
/// </summary>
public sealed class AMissingDependencyIsNotAnInvitationTests
{
    private static BuildPlan Plan() =>
        new(UnitContract.Minimal("Unit", AuthoringKind.Visualizer),
            [new Milestone("m1", "Build", [
                new BuildTask("t1", "Geometry", TaskKind.Maths, "Geometry.cs", "the layout maths", []),
                new BuildTask("t2", "Cells", TaskKind.Panel, "Cells.cs", "the cells", ["t1"]),
                new BuildTask("t3", "Kernel", TaskKind.Signal, "Unit.cs", "the unit", ["t1"]),
            ])],
            [],
            []);

    private static BuildTask Kernel(BuildPlan plan) => plan.Tasks.Single(t => t.Id == "t3");

    [Fact]
    public void An_absent_dependency_is_named_and_forbidden()
    {
        // The whole fix. Silence is the worst of the three answers: present, absent, or unmentioned.
        var context = new SwarmContext("a footprint");
        var plan = Plan();

        var message = context.ComposeBuild(Kernel(plan), plan);

        message.Should().Contain("NOT YET WRITTEN — Geometry.cs");
        message.Should().Contain("DO NOT DEFINE IT HERE");
    }

    [Fact]
    public void A_dependency_that_exists_is_still_sent_whole()
    {
        // The behaviour that already worked must not have been traded away: a builder writing against a
        // helper needs its actual members, not a description of them.
        var plan = Plan();
        var context = new SwarmContext("a footprint",
            [new StrategyFile("Geometry.cs", "public sealed class Geometry { public double X => 1d; }")]);

        var message = context.ComposeBuild(Kernel(plan), plan);

        message.Should().Contain("ALREADY WRITTEN — Geometry.cs");
        message.Should().Contain("public double X => 1d;");
        message.Should().NotContain("NOT YET WRITTEN");
    }

    [Fact]
    public void Every_file_is_told_what_it_does_not_own()
    {
        // Stated whether or not anything is missing, because the failure is the same either way and a
        // rule that appears only in the broken case is a rule nobody learns.
        var plan = Plan();
        var context = new SwarmContext("a footprint",
        [
            new StrategyFile("Geometry.cs", "public sealed class Geometry { }"),
            new StrategyFile("Cells.cs", "public sealed class Cells { }"),
        ]);

        var message = context.ComposeBuild(Kernel(plan), plan);

        message.Should().Contain("OWNED BY OTHER FILES");
        message.Should().Contain("Geometry.cs");
        message.Should().Contain("Cells.cs");
        message.Should().NotContain("Unit.cs", "a file is not told to avoid declaring its own type");
    }

    [Fact]
    public void A_repair_is_told_the_same_thing()
    {
        // WHERE THE DUPLICATE ACTUALLY GOT IN, after ComposeBuild had learned to prevent it. The
        // furniture helper stalled five times out of six, so the kernel was repaired against "CS0103:
        // the name ChartFurniture does not exist" — and the obvious repair for a name that does not
        // exist is to define it. When the real file finally landed, the unit had CS0101.
        var plan = Plan();
        var context = new SwarmContext("a footprint",
            [new StrategyFile("Unit.cs", "public sealed class Unit { }")]);

        var message = context.ComposeRepair(
            Kernel(plan),
            [new VerificationFinding("CS0103", "The name 'Geometry' does not exist.", "Fix it.")],
            plan);

        message.Should().Contain("NOT YET WRITTEN — and not yours to write");
        message.Should().Contain("Geometry.cs");
        message.Should().Contain("leave them undefined");
    }

    [Fact]
    public void A_repair_with_nothing_missing_is_not_warned_about_nothing()
    {
        // Every file exists, so there is nothing to absorb and nothing to say. A warning that fires
        // always is a warning that is read never.
        var plan = Plan();
        var context = new SwarmContext("a footprint",
        [
            new StrategyFile("Geometry.cs", "public sealed class Geometry { }"),
            new StrategyFile("Cells.cs", "public sealed class Cells { }"),
            new StrategyFile("Unit.cs", "public sealed class Unit { }"),
        ]);

        context.ComposeRepair(
                Kernel(plan),
                [new VerificationFinding("CS1002", "; expected", "Add it.")],
                plan)
            .Should().NotContain("NOT YET WRITTEN");
    }

    [Fact]
    public void A_single_task_plan_is_not_told_to_avoid_itself()
    {
        // The fallback owns everything there is. A list of files it must not touch would be a list of
        // its own work.
        var plan = BuildPlan.Single("a footprint", AuthoringKind.Visualizer);
        var context = new SwarmContext("a footprint");

        context.ComposeBuild(plan.Tasks[0], plan).Should().NotContain("OWNED BY OTHER FILES");
    }
}
