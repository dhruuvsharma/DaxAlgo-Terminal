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

    /// <summary>Each builder answers with ITS OWN file. Giving them all the same one would let the
    /// merge rule rename a stray unit into the missing file's place and quietly hide the gap.</summary>
    private static string Answer(string role) => Planner(role)
        ? Json(ThreeTaskPlan)
        : role.Contains("Maths.cs", StringComparison.Ordinal)
            ? File("Maths.cs", "public sealed class Maths { public double Push(double v) => v; }")
            : role.Contains("Grid.cs", StringComparison.Ordinal)
                ? File("Grid.cs", "public sealed class Grid { public void Paint() { } }")
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
    public async Task The_task_that_wrote_nothing_is_asked_again()
    {
        // Routing is by file name, and a file that does not exist is named by no diagnostic — so the
        // builder that returned nothing was never asked twice. Measured: the KERNEL task was the one
        // that failed, the gate said "no public class implementing IStrategyKernel" (which names no
        // file) beside two ordinary compile errors (which do), the two named files were repaired, and
        // the missing unit was not. Four correct helpers and no entry point.
        var client = new FailsOn(Answer, "Unit.cs");
        var seen = new List<SwarmEvent>();

        await new SwarmRunner(client, new UnitGate(new RoslynStrategyCompiler(), "test.unit", "Test unit"))
            .RunAsync(
                new SwarmRequest("a correlation matrix", "PACK", AuthoringKind.Strategy,
                    new SwarmBudget(MaxParallel: 2, MaxRounds: 1, MaxTasks: 8)),
                new Progress<SwarmEvent>(seen.Add));

        await Task.Delay(50);

        // ASKED AGAIN is the property, not "flagged as a repair" — a file that was never written is
        // written, not repaired, and the round that asks for it is the repair round doing its job.
        seen.OfType<SwarmEvent.TaskStarted>()
            .Count(s => s.Task.OwnedFile == "Unit.cs")
            .Should().BeGreaterThan(1, "the file nobody wrote is the one the repair round exists for");
    }

    [Fact]
    public async Task It_is_asked_to_WRITE_the_missing_file_rather_than_to_repair_it()
    {
        // A repair for a file that does not exist is not a repair, and the Fixer prompt cannot be
        // honoured: "change as little as possible" in a file the model cannot see, against a diagnostic
        // (no hostable class) that names no file. Measured on the opening-range brief — the kernel was
        // asked four times, spent 76,000 to 115,000 output tokens each time, and never produced a line.
        //
        // So the ROUND is a repair and the TURN is a build. The board says so too: a row that has never
        // been written reads "building", because that is what is happening.
        var client = new Records(Answer, fail: "Unit.cs");
        var seen = new List<SwarmEvent>();

        await new SwarmRunner(client, new UnitGate(new RoslynStrategyCompiler(), "test.unit", "Test unit"))
            .RunAsync(
                new SwarmRequest("a correlation matrix", "PACK", AuthoringKind.Strategy,
                    new SwarmBudget(MaxParallel: 2, MaxRounds: 1, MaxTasks: 8)),
                new Progress<SwarmEvent>(seen.Add));

        await Task.Delay(50);

        client.Roles.Where(r => r.Contains("Unit.cs", StringComparison.Ordinal))
            .Should().OnlyContain(r => r.Contains("YOUR ROLE: Builder", StringComparison.Ordinal),
                "there is nothing to fix — the file was never written");

        seen.OfType<SwarmEvent.TaskStarted>()
            .Where(s => s.Task.OwnedFile == "Unit.cs")
            .Should().OnlyContain(s => !s.IsRepair, "the board must not call a first write a repair");
    }

    /// <summary>Fails one owned file and remembers every role instruction it was sent.</summary>
    private sealed class Records(Func<string, string> reply, string fail) : IStrategyCodegenClient
    {
        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;

        public System.Collections.Concurrent.ConcurrentQueue<string> Sent { get; } = new();

        public IReadOnlyList<string> Roles => [.. Sent];

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default)
        {
            var role = request.RoleInstruction ?? string.Empty;
            Sent.Enqueue(role);

            if (role.Contains(fail, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(StrategyCodegenResponse.Fail(
                    "The provider spent the whole generation reasoning and never started an answer."));

            var text = reply(role);
            return Task.FromResult(StrategyCodegenResponse.Ok(
                CodegenCodeExtractor.ExtractFiles(text), text, new CodegenUsage(10, 20)));
        }
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
