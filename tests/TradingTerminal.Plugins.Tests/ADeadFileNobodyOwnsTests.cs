using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// A file that was in the editor when the run started, that no task owns, and that does not compile.
///
/// <para><b>This is the shape of a real failure, recovered from a user's own session.</b> Four generated
/// files, all four correct, all four thrown away — because a fifth file was in the compile set and had
/// two errors in it. Three repair rounds each concluded the same thing, in the model's own words: "omit
/// Strategy.cs; return the four correct files verbatim." Not one of them could. Repairs are routed by
/// owner; an orphan has none; and the fixer that got the findings anyway is told in its own prompt not
/// to touch files that are not its. The budget ran out arguing with a file nobody could reach.</para>
///
/// <para><b>The compiler decides, not a model.</b> Dropping a file is the only destructive thing this
/// pipeline does, so it is done on evidence: the set without it is compiled, and it is kept only if it
/// climbs strictly higher. That is what separates "this file is dead weight" from "this file is the
/// unit", and the second test below is the one that matters — a file carrying the only hostable class
/// takes the whole unit down with it, so it is kept and repaired like anything else.</para>
/// </summary>
public sealed class ADeadFileNobodyOwnsTests
{
    private sealed class Scripted(Func<string, string> reply) : IStrategyCodegenClient
    {
        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default)
        {
            var text = reply(request.RoleInstruction ?? string.Empty);
            return Task.FromResult(StrategyCodegenResponse.Ok(
                CodegenCodeExtractor.ExtractFiles(text), text, new CodegenUsage(10, 20)));
        }
    }

    private static bool Planner(string role) => role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal);

    private static SwarmRunner Runner(IStrategyCodegenClient client) =>
        new(client, new UnitGate(new RoslynStrategyCompiler(), "test.unit", "Test unit"));

    private static string Json(string body) => "```json\n" + body + "\n```";

    private static string File(string name, string body) =>
        "```csharp\n// file: " + name + "\n" + body + "\n```";

    /// <summary>One task, owning one file — so anything else in the build is an orphan.</summary>
    private const string OneTaskPlan = """
        {
          "contract": { "typeName": "Unit", "dataRequirement": "Bars" },
          "milestones": [
            { "id": "m1", "title": "The unit", "tasks": [
              { "id": "t1", "title": "Kernel", "kind": "Signal", "ownedFile": "Unit.cs",
                "intent": "the kernel", "dependsOn": [] }]}
          ],
          "rubric": [],
          "openQuestions": []
        }
        """;

    /// <summary>A real, complete visualizer — the thing the run is actually meant to produce.</summary>
    private const string GoodUnit = """
        public sealed class Unit : IVisualizer
        {
            private readonly List<double> _mids = new(64);

            public StrategyParameterSchema Schema { get; } = new(
                StrategyParameter.Int("period", "Period", 10, min: 2, max: 200));

            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.L1;

            public Task OnStartAsync(IVisualizerContext context, CancellationToken ct)
            {
                _ = context.Parameters.GetInt("period");
                _mids.Clear();
                return Task.CompletedTask;
            }

            public Task OnQuoteAsync(Quote quote, IVisualizerContext context, CancellationToken ct)
            {
                if (_mids.Count == 64) _mids.RemoveAt(0);
                _mids.Add((quote.Bid + quote.Ask) / 2d);
                return Task.CompletedTask;
            }

            public void Draw(IRenderSurface surface)
            {
                using var panel = surface.Panel("Mid", RenderPanelKind.Chart);
                surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent)));
                surface.Text(8d, 20d, "mid");

                using var series = surface.Series("Mid", RenderSeriesKind.Line);
                for (var i = 0; i < _mids.Count; i++) surface.Push(i, _mids[i]);
            }
        }
        """;

    /// <summary>The scaffold that actually did this: the retired contract, which does not resolve.</summary>
    private static readonly StrategyFile DeadScaffold = new(
        "Strategy.cs",
        """
        public sealed class MyStrategy : IOrderRoutedStrategy
        {
            public IOrderRoutedStrategy? Self => null;
        }
        """);

    private static SwarmRequest Request(params StrategyFile[] existing) =>
        new("a footprint window", "PACK", AuthoringKind.Visualizer,
            new SwarmBudget(MaxParallel: 1, MaxRounds: 2, MaxTasks: 4),
            Existing: existing);

    [Fact]
    public async Task The_run_delivers_instead_of_burning_its_budget_on_a_file_it_cannot_reach()
    {
        var client = new Scripted(role => Planner(role) ? Json(OneTaskPlan) : File("Unit.cs", GoodUnit));

        var run = await Runner(client).RunAsync(Request(DeadScaffold));

        run.Outcome.Should().Be(SwarmOutcome.Delivered);
        run.Files.Select(f => f.Name).Should().Equal("Unit.cs");
        run.Report!.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task It_is_reported_rather_than_disappearing()
    {
        // Removing somebody's file is the one thing here that destroys work rather than adding to it.
        var client = new Scripted(role => Planner(role) ? Json(OneTaskPlan) : File("Unit.cs", GoodUnit));

        var seen = new List<SwarmEvent>();
        await Runner(client).RunAsync(
            Request(DeadScaffold), progress: new Progress<SwarmEvent>(seen.Add));

        // Progress<T> posts asynchronously; give the callbacks their turn before reading.
        await Task.Delay(50);

        var dropped = seen.OfType<SwarmEvent.Dropped>().Should().ContainSingle().Subject;
        dropped.Files.Should().Equal("Strategy.cs");
        dropped.Why.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_file_the_unit_actually_needs_is_never_dropped()
    {
        // THE GUARD, and the reason the compiler decides rather than a rule. This orphan is the only
        // hostable class in the build: without it there is no unit at all. "Drop whatever is failing"
        // would delete it and report success on an empty shell.
        var orphanIsTheUnit = new StrategyFile("Mine.cs", GoodUnit);

        var client = new Scripted(role => Planner(role)
            ? Json(OneTaskPlan)
            : File("Unit.cs", "public sealed class Helper { public int Broken => nope; }"));

        var run = await Runner(client).RunAsync(Request(orphanIsTheUnit));

        run.Files.Should().Contain(f => f.Name == "Mine.cs");
        run.Outcome.Should().NotBe(SwarmOutcome.Delivered);
    }

    [Fact]
    public async Task A_failing_orphan_beside_a_failing_unit_is_still_kept_until_the_unit_is_fixed()
    {
        // Dropping it would raise nothing: the build fails either way, so there is no evidence the file
        // is the problem. Removing work on a hunch is exactly what the compiler check is there to stop.
        var client = new Scripted(role => Planner(role)
            ? Json(OneTaskPlan)
            : File("Unit.cs", "public sealed class Helper { public int Broken => nope; }"));

        var run = await Runner(client).RunAsync(Request(DeadScaffold));

        run.Files.Should().Contain(f => f.Name == "Strategy.cs");
    }

    [Fact]
    public void The_single_task_fallback_owns_everything_so_nothing_is_ever_an_orphan()
    {
        // Its one task owns the whole unit under whatever names the builder chose, so there is no such
        // thing as a file it does not own — and shedding one would be deleting its own work.
        var context = new SwarmContext("a footprint window", [DeadScaffold]);

        context.Orphans(BuildPlan.Single("a footprint window", AuthoringKind.Visualizer)).Should().BeEmpty();
    }
}
