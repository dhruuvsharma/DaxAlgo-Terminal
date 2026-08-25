using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Agents;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The loop (#48), driven from a fake client so a whole run can be asserted rather than observed.
///
/// <para>A loop that can only be watched against a live provider is a loop nobody will change — every
/// alteration would cost tokens and come back different.</para>
/// </summary>
public sealed class AgentLoopTests
{
    private const string Fenced = "```csharp\n// file: X.cs\npublic sealed class X { }\n```";

    private static VerificationReport Passing() => new(
    [
        VerificationStep.Pass(VerificationRung.Compile),
        VerificationStep.Pass(VerificationRung.Shape),
        VerificationStep.Pass(VerificationRung.Lifecycle),
    ]);

    private static VerificationReport Failing(VerificationRung rung) => new(
    [
        VerificationStep.Pass(VerificationRung.Compile),
        VerificationStep.Fail(rung, new VerificationFinding("x.y", "wrong", "fix it")),
    ]);

    /// <summary>A judge returning a scripted verdict per turn, advancing state the way the real one does.</summary>
    private static Func<IReadOnlyList<StrategyFile>, VerdictAndState> Judge(
        RoutingState start, params VerificationReport[] verdicts)
    {
        var index = 0;
        var state = start;
        return _ =>
        {
            var report = verdicts[Math.Min(index++, verdicts.Length - 1)];
            state = LadderFeedback.Advance(state, report) with { Reviewed = report.Passed };
            return new VerdictAndState(report, state);
        };
    }

    private static StrategyCodegenResponse Wrote() => new(
        Success: true,
        Code: "public sealed class X { }",
        RawText: Fenced,
        Error: null,
        Files: [new StrategyFile("X.cs", "public sealed class X { }")]);

    [Fact]
    public async Task ACleanRunDelivers()
    {
        var start = new RoutingState(HasSpec: true);
        var loop = new AgentLoop(new ScriptedClient(Wrote()), Judge(start, Passing()));

        var run = await loop.RunAsync("build me an EMA cross", "PACK", start);

        run.Outcome.Should().Be(AgentRunOutcome.Delivered);
        run.Turns.Should().NotBeEmpty();
    }

    [Fact]
    public async Task TheFirstTurnGoesWhereThePriorSays()
    {
        var start = new RoutingState(HasSpec: true);
        var loop = new AgentLoop(new ScriptedClient(Wrote()), Judge(start, Passing()));

        var run = await loop.RunAsync("brief", "PACK", start);

        run.Turns[0].Role.Should().Be(AgentRole.Coder, "a spec needing no maths goes straight to code");
    }

    [Fact]
    public async Task AnAgentAskingAQuestionPausesTheRunRatherThanFailingIt()
    {
        // A reply with no code from a role that does not write code is the job, not a failed generation.
        var asked = new StrategyCodegenResponse(
            Success: true, Code: null, RawText: "Which instrument, and over what timeframe?", Error: null);

        var run = await new AgentLoop(new ScriptedClient(asked), Judge(new RoutingState(), Passing()))
            .RunAsync("make me money", "PACK", new RoutingState());

        run.Outcome.Should().Be(AgentRunOutcome.AwaitingUser);
        run.Turns.Should().ContainSingle().Which.Role.Should().Be(AgentRole.Interviewer);
    }

    [Fact]
    public async Task AFailedRungRoutesTheNextTurnToWhoeverOwnsIt()
    {
        var start = new RoutingState(HasSpec: true, MustDraw: true);
        var loop = new AgentLoop(
            new ScriptedClient(Wrote(), Wrote(), Wrote()),
            Judge(start, Failing(VerificationRung.DrawProbe), Passing()));

        var run = await loop.RunAsync("brief", "PACK", start);

        run.Turns[1].Role.Should().Be(AgentRole.Painter, "a draw-probe failure is the Painter's own work");
    }

    [Fact]
    public async Task TheBudgetIsHonoured()
    {
        // It is the user's money, and a loop that can spend without limit will.
        var start = new RoutingState(HasSpec: true, MustDraw: true);
        var loop = new AgentLoop(
            new ScriptedClient(Wrote()),
            Judge(start, Failing(VerificationRung.Lifecycle)));

        var run = await loop.RunAsync("brief", "PACK", start, maxTurns: 3);

        run.Outcome.Should().Be(AgentRunOutcome.BudgetExhausted);
        run.Turns.Should().HaveCount(3);
    }

    [Fact]
    public async Task ReliabilityMovesWithTheVerdict()
    {
        var start = new RoutingState(HasSpec: true, MustDraw: true);
        var loop = new AgentLoop(
            new ScriptedClient(Wrote()),
            Judge(start, Failing(VerificationRung.Lifecycle)));

        await loop.RunAsync("brief", "PACK", start, maxTurns: 4);

        loop.Reliability.Of(AgentRole.Coder).Should().BeLessThan(AgentReliability.NeutralPrior);
        loop.Reliability.ObservationsFor(AgentRole.Coder).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task EveryTurnRecordsTheReasoningBehindIt()
    {
        // A trajectory of bare agent names is one nobody can distil later.
        var start = new RoutingState(HasSpec: true);
        var loop = new AgentLoop(new ScriptedClient(Wrote()), Judge(start, Passing()));

        var run = await loop.RunAsync("brief", "PACK", start);

        run.Turns.Should().OnlyContain(turn => turn.Weights.Count > 0);
    }

    [Fact]
    public async Task TheSharedPackIsSentUntouchedAndTheRoleTravelsSeparately()
    {
        // The composition that keeps one cached prefix across six agents. Appending the role to the pack
        // would make every agent a distinct prefix and re-bill the whole document on each switch.
        var client = new ScriptedClient(Wrote());
        var start = new RoutingState(HasSpec: true);

        await new AgentLoop(client, Judge(start, Passing())).RunAsync("brief", "SHARED", start);

        client.Seen.Should().NotBeEmpty();
        client.Seen.Should().OnlyContain(r => r.SystemContext == "SHARED");
        client.Seen.Should().OnlyContain(r => r.RoleInstruction!.Contains("YOUR ROLE"));
    }

    [Fact]
    public async Task AProviderFailureStopsTheRunAndSaysWhy()
    {
        var start = new RoutingState(HasSpec: true);
        var failed = new StrategyCodegenResponse(
            Success: false, Code: null, RawText: null, Error: "no api key");

        var run = await new AgentLoop(new ScriptedClient(failed), Judge(start, Passing()))
            .RunAsync("brief", "PACK", start);

        run.Outcome.Should().Be(AgentRunOutcome.ProviderFailed);
        run.Error.Should().Contain("no api key");
    }

    /// <summary>Replays canned responses, repeating the last, and keeps every request for inspection.</summary>
    private sealed class ScriptedClient(params StrategyCodegenResponse[] replies) : IStrategyCodegenClient
    {
        private int _index;

        public List<StrategyCodegenRequest> Seen { get; } = [];

        public string ProviderId => "scripted";

        public string DisplayName => "Scripted";

        public bool IsAvailable => true;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default)
        {
            Seen.Add(request);
            return Task.FromResult(replies[Math.Min(_index++, replies.Length - 1)]);
        }
    }
}
