using System.Collections.Concurrent;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;
using TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// A finding that names no file is still routed to the task that owns the behaviour, even while another
/// task has not written its file yet.
///
/// <para><b>Measured on a live Blocks run.</b> The unit compiled and its <c>StartAsync</c> threw — a
/// lifecycle finding, which names no file — while the page builder had produced nothing. Repair targets
/// were "the missing tasks plus the tasks a finding names", so every round rebuilt the page and the unit
/// was never once asked to fix the exception. Four rounds, the same finding each time, and a stall.</para>
/// </summary>
public sealed class AFindingThatNamesNoFileStillReachesItsOwnerTests
{
    private const string Plan = """
        {
          "contract": {
            "typeName": "VolumeUnit",
            "topics": [ { "name": "state", "direction": "to-page", "payload": "{ last: number }" } ]
          },
          "milestones": [
            { "id": "m1", "title": "Build", "tasks": [
              { "id": "t1", "title": "The unit", "kind": "Signal", "ownedFile": "VolumeUnit.cs",
                "blocks": ["unit", "ui"], "intent": "the unit", "dependsOn": [] },
              { "id": "t2", "title": "The page", "kind": "Ui", "ownedFile": "ui/index.html",
                "blocks": ["ui"], "intent": "the page", "dependsOn": [] }
            ]}
          ],
          "rubric": [],
          "openQuestions": []
        }
        """;

    /// <summary>Plans, writes the unit, and never writes the page.</summary>
    private sealed class PageNeverArrives : IStrategyCodegenClient
    {
        public ConcurrentQueue<string> Roles { get; } = new();

        public string ProviderId => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;

        public Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
        {
            var role = request.RoleInstruction ?? string.Empty;
            Roles.Enqueue(role);

            var text = role.Contains("YOUR ROLE: Planner", StringComparison.Ordinal) ? "```json\n" + Plan + "\n```"
                : role.Contains("VolumeUnit.cs", StringComparison.Ordinal) ? "```csharp\n// file: VolumeUnit.cs\npublic sealed class VolumeUnit { }\n```"
                : "I will think about the page some more.";

            return Task.FromResult(StrategyCodegenResponse.Ok(CodegenCodeExtractor.ExtractFiles(text), text, new CodegenUsage(10, 20)));
        }
    }

    /// <summary>Says the unit threw on start, whatever it is shown — a finding with no file.</summary>
    private sealed class StartThrows : IUnitGate
    {
        public Task<GateResult> RunAsync(IReadOnlyList<StrategyFile> files, CancellationToken ct = default) =>
            Task.FromResult(new GateResult(
                new VerificationReport(
                [
                    VerificationStep.Pass(VerificationRung.Compile),
                    VerificationStep.Fail(VerificationRung.Lifecycle,
                        new VerificationFinding("start.threw", "StartAsync threw.", "Register handlers and return.")),
                ]),
                Compile: null));
    }

    /// <summary>Passes whatever it is shown — as the real gate does for a unit with no page.</summary>
    private sealed class AlwaysPasses : IUnitGate
    {
        public Task<GateResult> RunAsync(IReadOnlyList<StrategyFile> files, CancellationToken ct = default) =>
            Task.FromResult(new GateResult(
                new VerificationReport([VerificationStep.Pass(VerificationRung.Compile), VerificationStep.Pass(VerificationRung.Lifecycle)]),
                Compile: null));
    }

    [Fact]
    public async Task AUnitWhosePlannedPageWasNeverWrittenIsNotDelivered()
    {
        // The gate cannot know a page was planned: a unit with no page is legitimate, so it passes.
        // Measured: the page builder ran out of budget every round while the unit cleared the ladder alone.
        var client = new PageNeverArrives();

        var run = await new SwarmRunner(client, new AlwaysPasses(), dialect: new BlocksSwarmDialect())
            .RunAsync(new SwarmRequest(
                "a volume unit", "PACK", AuthoringKind.Visualizer,
                new SwarmBudget(MaxParallel: 1, MaxRounds: 2, MaxTasks: 4)));

        run.Outcome.Should().NotBe(SwarmOutcome.Delivered);
        run.Summary.Should().Contain("Never written: ui/index.html");
    }

    [Fact]
    public async Task TheUnitIsAskedToFixItsStartWhileThePageIsStillMissing()
    {
        var client = new PageNeverArrives();

        await new SwarmRunner(client, new StartThrows(), dialect: new BlocksSwarmDialect())
            .RunAsync(new SwarmRequest(
                "a volume unit", "PACK", AuthoringKind.Visualizer,
                new SwarmBudget(MaxParallel: 1, MaxRounds: 1, MaxTasks: 4)));

        client.Roles.Should().Contain(
            role => role.Contains("YOUR ROLE: Fixer", StringComparison.Ordinal) && role.Contains("VolumeUnit.cs", StringComparison.Ordinal),
            "start.threw names no file, and the class implementing IUnit is who answers for it");
        client.Roles.Count(role => role.Contains("the page", StringComparison.Ordinal))
            .Should().BeGreaterThan(1, "the page that was never written is still rebuilt as well");
    }
}
