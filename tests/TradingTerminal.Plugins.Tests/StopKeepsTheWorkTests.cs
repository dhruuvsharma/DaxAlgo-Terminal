using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Pressing Stop.
///
/// <para><b><see cref="SwarmOutcome.Cancelled"/> was declared and never produced.</b> Its own summary
/// says "the user stopped it — whatever was built is kept", and nothing in the runner ever returned it:
/// the cancellation threw straight out of <c>RunAsync</c>, taking every file the builders had already
/// written with it. On a slow reasoning model that is many minutes of thinking, and real money, for
/// nothing — and the user is left with an empty editor and no account of what happened.</para>
///
/// <para>Found by running the pipeline against a live provider with a wall-clock bound on it, and
/// realising the bound would have discarded the run rather than delivered it.</para>
/// </summary>
public sealed class StopKeepsTheWorkTests
{
    /// <summary>Answers builders, and trips the token on whichever call the test names.</summary>
    private sealed class StopsOn(int call, CancellationTokenSource cts, Func<string, string> reply)
        : IStrategyCodegenClient
    {
        private int _calls;

        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var role = request.RoleInstruction ?? string.Empty;
            var text = reply(role);

            // Answer first, THEN stop — which is the real sequence. The user presses Stop while a later
            // call is in flight, and the work that already came back is what has to survive.
            if (Interlocked.Increment(ref _calls) >= call) cts.Cancel();

            return Task.FromResult(StrategyCodegenResponse.Ok(
                CodegenCodeExtractor.ExtractFiles(text), text, new CodegenUsage(10, 20)));
        }
    }

    private static bool Planner(string role) => role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal);

    private static string Json(string body) => "```json\n" + body + "\n```";

    private static string File(string name, string body) =>
        "```csharp\n// file: " + name + "\n" + body + "\n```";

    /// <summary>Two tasks in two milestones, so the second builder is a separate, interruptible call.</summary>
    private const string TwoTaskPlan = """
        {
          "contract": { "typeName": "Unit", "dataRequirement": "Bars" },
          "milestones": [
            { "id": "m1", "title": "Maths", "tasks": [
              { "id": "t1", "title": "Smoother", "kind": "Maths", "ownedFile": "Smoother.cs",
                "intent": "an EMA", "dependsOn": [] }]},
            { "id": "m2", "title": "The unit", "tasks": [
              { "id": "t2", "title": "Kernel", "kind": "Signal", "ownedFile": "Unit.cs",
                "intent": "the kernel", "dependsOn": ["t1"] }]}
          ],
          "rubric": [],
          "openQuestions": []
        }
        """;

    private static SwarmRequest Request() =>
        new("an order book window", "PACK", AuthoringKind.Strategy,
            new SwarmBudget(MaxParallel: 1, MaxRounds: 1, MaxTasks: 4));

    private static SwarmRunner Runner(IStrategyCodegenClient client) =>
        new(client, new UnitGate(new RoslynStrategyCompiler(), "test.unit", "Test unit"));

    [Fact]
    public async Task A_stopped_run_keeps_the_files_that_were_already_written()
    {
        // The whole point. Two calls in: the plan, and one builder that answered. Its file is the
        // user's, it has been paid for, and it must come back.
        using var cts = new CancellationTokenSource();
        var client = new StopsOn(2, cts, role => Planner(role)
            ? Json(TwoTaskPlan)
            : File("Smoother.cs", "public sealed class Smoother { public double Push(double v) => v; }"));

        var run = await Runner(client).RunAsync(Request(), ct: cts.Token);

        run.Outcome.Should().Be(SwarmOutcome.Cancelled);
        run.Files.Should().ContainSingle(f => f.Name == "Smoother.cs");
        run.Summary.Should().Contain("Smoother.cs", "the user is owed an account of what was kept");
    }

    [Fact]
    public async Task Stopping_during_the_plan_is_reported_rather_than_thrown()
    {
        // Nothing was built, which is not the same as nothing happened: the planner's turn was billed.
        // A thrown exception here is a crash the pane has to guess the meaning of.
        using var cts = new CancellationTokenSource();
        var client = new StopsOn(1, cts, _ => Json(TwoTaskPlan));

        var run = await Runner(client).RunAsync(Request(), ct: cts.Token);

        run.Outcome.Should().Be(SwarmOutcome.Cancelled);
        run.Files.Should().BeEmpty();
        run.Usage.TotalTokens.Should().BeGreaterThan(0, "the planner's turn was paid for");
    }

    [Fact]
    public async Task A_stopped_run_still_says_it_finished()
    {
        // The pane's status strip and the transcript both close on Finished. Without it a stopped run
        // leaves a spinner turning over a run that ended.
        using var cts = new CancellationTokenSource();
        var client = new StopsOn(2, cts, role => Planner(role)
            ? Json(TwoTaskPlan)
            : File("Smoother.cs", "public sealed class Smoother { }"));

        var seen = new List<SwarmEvent>();
        await Runner(client).RunAsync(Request(), new Progress<SwarmEvent>(seen.Add), ct: cts.Token);
        await Task.Delay(50);

        seen.OfType<SwarmEvent.Finished>().Should()
            .ContainSingle().Which.Outcome.Should().Be(SwarmOutcome.Cancelled);
    }
}
