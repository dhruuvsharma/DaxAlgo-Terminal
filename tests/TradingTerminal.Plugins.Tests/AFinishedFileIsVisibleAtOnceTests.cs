using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// When a finished file becomes visible.
///
/// <para><b>"The code appears as each task writes it" was true of the event and false of when it was
/// raised.</b> <c>TaskFinished</c> carries the files precisely so the editor can fill during a
/// multi-minute fan-out — and the runner raised it only after <i>every</i> task in the wave had
/// returned. Measured on a real four-way fan-out against a slow model: the wave ran from 32:23 to
/// 01:11:59 and all four files appeared at 01:11:59. The first was finished, correct, and invisible for
/// the better part of forty minutes, which is exactly the "I can't see any code" the batching was
/// supposed to cure.</para>
///
/// <para>The merge is still serialised — the context and the usage total are shared — so this buys
/// visibility without buying a data race.</para>
/// </summary>
public sealed class AFinishedFileIsVisibleAtOnceTests
{
    /// <summary>Answers one task immediately and holds the other until the test lets go.</summary>
    private sealed class Holds(string held, Func<string, string> reply) : IStrategyCodegenClient
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;

        public void Release() => _release.TrySetResult();

        public async Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default)
        {
            var role = request.RoleInstruction ?? string.Empty;

            if (role.Contains(held, StringComparison.OrdinalIgnoreCase))
                await _release.Task.ConfigureAwait(false);

            var text = reply(role);
            return StrategyCodegenResponse.Ok(
                CodegenCodeExtractor.ExtractFiles(text), text, new CodegenUsage(10, 20));
        }
    }

    private static bool Planner(string role) => role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal);

    private static string Json(string body) => "```json\n" + body + "\n```";

    private static string File(string name, string body) =>
        "```csharp\n// file: " + name + "\n" + body + "\n```";

    /// <summary>Two tasks, no dependency between them: one wave, two lanes.</summary>
    private const string OneWavePlan = """
        {
          "contract": { "typeName": "Unit", "dataRequirement": "Bars" },
          "milestones": [
            { "id": "m1", "title": "Both at once", "tasks": [
              { "id": "t1", "title": "Quick", "kind": "Maths", "ownedFile": "Quick.cs",
                "intent": "the fast one", "dependsOn": [] },
              { "id": "t2", "title": "Slow", "kind": "Panel", "ownedFile": "Slow.cs",
                "intent": "the slow one", "dependsOn": [] }]}
          ],
          "rubric": [],
          "openQuestions": []
        }
        """;

    [Fact]
    public async Task The_first_task_to_finish_is_reported_before_the_wave_does()
    {
        var client = new Holds("Slow.cs", role => Planner(role)
            ? Json(OneWavePlan)
            : role.Contains("Quick.cs", StringComparison.Ordinal)
                ? File("Quick.cs", "public sealed class Quick { }")
                : File("Slow.cs", "public sealed class Slow { }"));

        var quick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new SwarmRunner(
            client, new UnitGate(new RoslynStrategyCompiler(), "test.unit", "Test unit"));

        var run = runner.RunAsync(
            new SwarmRequest("two files", "PACK", AuthoringKind.Strategy,
                new SwarmBudget(MaxParallel: 2, MaxRounds: 0, MaxTasks: 4)),
            new Progress<SwarmEvent>(evt =>
            {
                if (evt is SwarmEvent.TaskFinished { Task.OwnedFile: "Quick.cs" } done && done.Wrote)
                    quick.TrySetResult();
            }));

        // THE ASSERTION IS THE WAIT. The fast task's file has to arrive while the slow one is still in
        // flight; batching by wave makes this hang until the timeout.
        var arrived = await Task.WhenAny(quick.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        arrived.Should().Be(quick.Task, "a finished file must not wait for the rest of its wave");

        client.Release();
        await run;
    }

    [Fact]
    public async Task Its_files_are_carried_on_the_event_so_the_editor_can_show_them()
    {
        // Visibility means the file itself, not a name. The event exists so the pane can call SetFiles.
        var client = new Holds("Slow.cs", role => Planner(role)
            ? Json(OneWavePlan)
            : role.Contains("Quick.cs", StringComparison.Ordinal)
                ? File("Quick.cs", "public sealed class Quick { }")
                : File("Slow.cs", "public sealed class Slow { }"));

        var carried = new TaskCompletionSource<IReadOnlyList<StrategyFile>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new SwarmRunner(
            client, new UnitGate(new RoslynStrategyCompiler(), "test.unit", "Test unit"));

        var run = runner.RunAsync(
            new SwarmRequest("two files", "PACK", AuthoringKind.Strategy,
                new SwarmBudget(MaxParallel: 2, MaxRounds: 0, MaxTasks: 4)),
            new Progress<SwarmEvent>(evt =>
            {
                if (evt is SwarmEvent.TaskFinished { Task.OwnedFile: "Quick.cs" } done && done.Wrote)
                    carried.TrySetResult(done.Files);
            }));

        var files = await carried.Task.WaitAsync(TimeSpan.FromSeconds(10));
        files.Should().Contain(f => f.Name == "Quick.cs");

        client.Release();
        await run;
    }
}
