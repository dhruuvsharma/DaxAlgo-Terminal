using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;
using TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// A run delivers the best version it reached, not the last one it happened to end on.
///
/// <para><b>A critic-driven repair is a model rewriting working code to satisfy a note</b>, and it can
/// make things worse. Measured on the opening-range brief: round 0 cleared seven rungs, the critics'
/// eleven notes were applied, and round 1 came back with two drawing faults that had not been there —
/// a panel-less primitive and a text collision. A later run ended its budget on a compile error, three
/// rounds after a version that compiled.</para>
///
/// <para>Handing over the worse unit while a better one existed minutes earlier is the single most
/// annoying way to lose an hour of somebody's build, and it is invisible: the transcript says the run
/// finished, and the editor holds the regression.</para>
/// </summary>
public sealed class TheBestVersionIsTheOneDeliveredTests
{
    /// <summary>Builds clean, then rewrites itself into something worse on every repair.</summary>
    private sealed class DegradesOnRepair(Func<string, string> build, Func<string, string> repair)
        : IStrategyCodegenClient
    {
        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default)
        {
            var role = request.RoleInstruction ?? string.Empty;
            var text = role.Contains("YOUR ROLE: Fixer", StringComparison.Ordinal) ? repair(role) : build(role);

            return Task.FromResult(StrategyCodegenResponse.Ok(
                CodegenCodeExtractor.ExtractFiles(text), text, new CodegenUsage(10, 20)));
        }
    }

    /// <summary>
    /// A critic that is never satisfied — which is what forces a round after the gate has passed.
    ///
    /// <para>Without one, a passing gate delivers immediately and no repair can regress anything, so a
    /// test written without a critic proves nothing at all. This regression exists only because critics
    /// keep a run going past the point where the ladder is already happy.</para>
    /// </summary>
    private sealed class NeverSatisfied : IUnitCritic
    {
        public string Id => "picky";
        public CriticPanel Panel => CriticPanel.Quant;
        public bool NeedsPicture => false;

        public Task<CriticVerdict> JudgeAsync(
            GauntletSubject subject, ReferenceBar bar, CancellationToken ct = default) =>
            Task.FromResult(new CriticVerdict(
                Id,
                Panel,
                [new VerificationFinding("picky.more", "It could be better.", "Make it better.")],
                "not satisfied"));
    }

    private static SwarmRunner Runner(IStrategyCodegenClient client) =>
        new(client,
            new UnitGate(new RoslynStrategyCompiler(), "test.unit", "Test unit"),
            gauntlet: new GauntletLoop([new NeverSatisfied()]));

    private static bool Planner(string role) => role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal);

    private static string Json(string body) => "```json\n" + body + "\n```";

    private static string File(string name, string body) =>
        "```csharp\n// file: " + name + "\n" + body + "\n```";

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

    /// <summary>A unit that compiles and draws — the version worth keeping.</summary>
    private const string Good = """
        public sealed class Unit : IStrategyKernel
        {
            private readonly List<double> _closes = new(64);

            public StrategyParameterSchema Schema { get; } = new(
                StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 500));

            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

            public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct)
            {
                _ = context.Parameters.GetInt("lookback");
                _closes.Clear();
                return Task.CompletedTask;
            }

            public Task OnBarAsync(OhlcvBar bar, IStrategyRuntimeContext context, CancellationToken ct)
            {
                if (_closes.Count == 64) _closes.RemoveAt(0);
                _closes.Add(bar.Close);
                return Task.CompletedTask;
            }

            public void Draw(IRenderSurface surface)
            {
                using var panel = surface.Panel("Unit", RenderPanelKind.Chart);
                surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent)));
                surface.Text(8d, 20d, "unit");

                using var series = surface.Series("Close", RenderSeriesKind.Line);
                for (var i = 0; i < _closes.Count; i++) surface.Push(i, _closes[i]);
            }
        }
        """;

    /// <summary>What a repair turned it into: it no longer compiles.</summary>
    private const string Worse = "public sealed class Unit : IStrategyKernel { public int Broken => nope; }";

    [Fact]
    public async Task A_repair_that_makes_things_worse_does_not_decide_what_ships()
    {
        // Round 0 clears the ladder; the critic is never satisfied, so a repair round runs anyway and
        // rewrites the unit into something that does not compile. What ships must be round 0's files.
        var client = new DegradesOnRepair(
            build: role => Planner(role) ? Json(OneTaskPlan) : File("Unit.cs", Good),
            repair: _ => File("Unit.cs", Worse));

        var run = await Runner(client).RunAsync(new SwarmRequest(
            "a breakout strategy", "PACK", AuthoringKind.Strategy,
            new SwarmBudget(MaxParallel: 1, MaxRounds: 3, MaxTasks: 4)));

        run.Compile!.Success.Should().BeTrue("a version that compiled was reached and must be the one kept");
        run.Files.Should().ContainSingle().Which.Content.Should().Contain("RenderSeriesKind.Line");
    }

    [Fact]
    public async Task The_best_version_is_what_the_editor_is_left_holding()
    {
        // Not just the verdict — the FILES. A report that says "it compiles" over source that does not
        // is worse than either answer on its own.
        var client = new DegradesOnRepair(
            build: role => Planner(role) ? Json(OneTaskPlan) : File("Unit.cs", Good),
            repair: _ => File("Unit.cs", Worse));

        var run = await Runner(client).RunAsync(new SwarmRequest(
            "a breakout strategy", "PACK", AuthoringKind.Strategy,
                new SwarmBudget(MaxParallel: 1, MaxRounds: 3, MaxTasks: 4)));

        run.Files.Should().NotContain(f => f.Content.Contains("nope", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_that_only_ever_got_worse_still_reports_what_it_has()
    {
        // The guard against over-reading the rule: when nothing ever cleared anything, there is no
        // better version to go back to and the run must still hand over what exists rather than nothing.
        var client = new DegradesOnRepair(
            build: role => Planner(role) ? Json(OneTaskPlan) : File("Unit.cs", Worse),
            repair: _ => File("Unit.cs", Worse));

        var run = await Runner(client).RunAsync(new SwarmRequest(
            "a breakout strategy", "PACK", AuthoringKind.Strategy,
                new SwarmBudget(MaxParallel: 1, MaxRounds: 2, MaxTasks: 4)));

        run.Files.Should().ContainSingle(f => f.Name == "Unit.cs");
        run.Compile!.Success.Should().BeFalse();
    }
}
