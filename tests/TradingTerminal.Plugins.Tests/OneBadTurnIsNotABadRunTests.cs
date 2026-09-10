using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// One builder failing, out of several.
///
/// <para><b>Measured on a real run, and it cost the whole thing.</b> Three tasks: the maths wrote its
/// file in half an hour, the panel spent its entire generation reasoning and never began an answer
/// (77,072 output tokens, billed), and the third task — the one owning the hostable class — was never
/// asked, because the panel's failure returned straight out of the run. One hour fifty-one minutes and
/// roughly 170,000 tokens produced a single orphan helper and a unit with no entry point.</para>
///
/// <para><b>The two failures are not alike, and that is the whole fix.</b> A missing key fails EVERY
/// call — that run ends having built nothing, and is still reported as a provider failure with the
/// provider's own words. A model that over-thinks one turn fails ONE call, and the machinery for a
/// missing file already exists: the gate notices, and the repair round routes somebody at it.</para>
/// </summary>
public sealed class OneBadTurnIsNotABadRunTests
{
    /// <summary>Answers builders, and fails whichever owned files the test names.</summary>
    private sealed class FailsOn(Func<string, string> reply, params string[] files) : IStrategyCodegenClient
    {
        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;

        /// <summary>Set to fail every call, the way a missing key does.</summary>
        public bool Always { get; init; }

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default)
        {
            var role = request.RoleInstruction ?? string.Empty;

            if (Always && !Planner(role))
                return Task.FromResult(StrategyCodegenResponse.Fail("401 unauthorized: no API key."));

            if (files.Any(f => role.Contains(f, StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(StrategyCodegenResponse.Fail(
                    "The provider spent the whole generation reasoning and never started an answer."));

            var text = reply(role);
            return Task.FromResult(StrategyCodegenResponse.Ok(
                CodegenCodeExtractor.ExtractFiles(text), text, new CodegenUsage(10, 20)));
        }
    }

    private static bool Planner(string role) => role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal);

    private static string Json(string body) => "```json\n" + body + "\n```";

    private static string File(string name, string body) =>
        "```csharp\n// file: " + name + "\n" + body + "\n```";

    /// <summary>The shape the real run had: a helper, a panel, and the hostable class last.</summary>
    private const string ThreeTaskPlan = """
        {
          "contract": { "typeName": "Unit", "dataRequirement": "Bars" },
          "milestones": [
            { "id": "m1", "title": "The numbers", "tasks": [
              { "id": "t1", "title": "Maths", "kind": "Maths", "ownedFile": "Maths.cs",
                "intent": "the maths", "dependsOn": [] },
              { "id": "t2", "title": "Panel", "kind": "Panel", "ownedFile": "Grid.cs",
                "intent": "the picture", "dependsOn": [] }]},
            { "id": "m2", "title": "The unit", "tasks": [
              { "id": "t3", "title": "Kernel", "kind": "Signal", "ownedFile": "Unit.cs",
                "intent": "the kernel", "dependsOn": [] }]}
          ],
          "rubric": [],
          "openQuestions": []
        }
        """;

    private const string GoodUnit = """
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

    private static string Answer(string role) => Planner(role)
        ? Json(ThreeTaskPlan)
        : role.Contains("Maths.cs", StringComparison.Ordinal)
            ? File("Maths.cs", "public sealed class Maths { public double Push(double v) => v; }")
            : File("Unit.cs", GoodUnit);

    private static SwarmRequest Request() =>
        new("a correlation matrix", "PACK", AuthoringKind.Strategy,
            new SwarmBudget(MaxParallel: 2, MaxRounds: 1, MaxTasks: 8));

    private static SwarmRunner Runner(IStrategyCodegenClient client) =>
        new(client, new UnitGate(new RoslynStrategyCompiler(), "test.unit", "Test unit"));

    [Fact]
    public async Task The_tasks_after_a_failed_one_still_run()
    {
        // The task that owns the hostable class was scheduled AFTER the one that failed. It has to be
        // asked: without it there is no unit at all, whatever else got written.
        var run = await Runner(new FailsOn(Answer, "Grid.cs")).RunAsync(Request());

        run.Files.Should().Contain(f => f.Name == "Unit.cs", "the run must reach the task that owns the unit");
        run.Files.Should().Contain(f => f.Name == "Maths.cs", "and keep what was already written");
        run.Compile!.Success.Should().BeTrue();
        run.Outcome.Should().Be(SwarmOutcome.Delivered);
    }

    [Fact]
    public async Task A_failed_builder_is_reported_as_a_task_that_wrote_nothing()
    {
        // Reported, not swallowed: the turn was billed, and a user looking at three rows must be able to
        // see which one produced nothing and why.
        var seen = new List<SwarmEvent>();
        await Runner(new FailsOn(Answer, "Grid.cs"))
            .RunAsync(Request(), new Progress<SwarmEvent>(seen.Add));

        await Task.Delay(50);

        var empty = seen.OfType<SwarmEvent.TaskFinished>()
            .Should().ContainSingle(f => !f.Wrote).Subject;

        empty.Task.OwnedFile.Should().Be("Grid.cs");
        empty.Note.Should().Contain("reasoning", "the provider's own account of it is what the user needs");
    }

    [Fact]
    public async Task A_provider_that_fails_every_call_is_still_a_provider_failure()
    {
        // The other half of the rule. A missing key is not a bad turn — retrying will not fix it — and
        // the run must say so in the provider's words rather than reporting an exhausted budget.
        var run = await Runner(new FailsOn(Answer, files: []) { Always = true }).RunAsync(Request());

        run.Outcome.Should().Be(SwarmOutcome.ProviderFailed);
        run.Files.Should().BeEmpty();
        run.Error.Should().Contain("401");
    }
}
