using FluentAssertions;
using TradingTerminal.Infrastructure.Strategies.Authoring.Agents;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Routing (#48) — who takes the next turn.
///
/// <para>All of this is a pure function of state, reliability and η: no provider, no key, no token
/// budget. That is why it could be built before any agent existed to be routed to, and why the
/// arithmetic below can be asserted rather than observed.</para>
/// </summary>
public sealed class AgentRoutingTests
{
    private static readonly AgentReliability Fresh = new();

    // ── the prior: what the state machine allows ────────────────────────────────────────────────

    [Fact]
    public void ABareBriefGoesToTheInterviewer()
    {
        AgentRouter.Choose(new RoutingState(), Fresh)!.Role.Should().Be(AgentRole.Interviewer);
    }

    [Fact]
    public void ASpecNeedingMathsGoesToTheQuant()
    {
        AgentRouter.Choose(new RoutingState(HasSpec: true, NeedsMaths: true), Fresh)!
            .Role.Should().Be(AgentRole.Quant);
    }

    [Fact]
    public void ASpecNeedingNoMathsGoesStraightToTheCoder()
    {
        AgentRouter.Choose(new RoutingState(HasSpec: true), Fresh)!.Role.Should().Be(AgentRole.Coder);
    }

    [Fact]
    public void CompilingWithoutThePictureItOwesGoesToThePainter()
    {
        AgentRouter.Choose(
            new RoutingState(HasSpec: true, HasCode: true, Compiles: true, MustDraw: true),
            Fresh)!.Role.Should().Be(AgentRole.Painter);
    }

    [Fact]
    public void AStrategyThatOwesNoPictureSkipsThePainter()
    {
        // Drawing is optional for a strategy. Routing to a Painter that has nothing to do would burn a
        // turn of the user's money to produce nothing.
        AgentRouter.Choose(
            new RoutingState(HasSpec: true, HasCode: true, Compiles: true, MustDraw: false),
            Fresh)!.Role.Should().Be(AgentRole.Reviewer);
    }

    [Fact]
    public void AFinishedSessionRoutesNowhere()
    {
        // The loop stops on an empty prior rather than on a role meaning "done", which could be selected
        // by mistake and would then have to do nothing convincingly.
        AgentRouter.Choose(
            new RoutingState(HasSpec: true, HasCode: true, Compiles: true, MustDraw: true, Draws: true, Reviewed: true),
            Fresh).Should().BeNull();
    }

    // ── failures outrank everything ─────────────────────────────────────────────────────────────

    [Fact]
    public void AFailedRungPreemptsWhateverElseWasNext()
    {
        // The artifact is wrong, so any other agent would be building on it.
        var state = new RoutingState(
            HasSpec: true, HasCode: true, Compiles: true, MustDraw: true, Draws: true,
            FailedAt: VerificationRung.Lifecycle);

        AgentRouter.Choose(state, Fresh)!.Role.Should().Be(AgentRole.Fixer);
    }

    [Theory]
    [InlineData(VerificationRung.DrawProbe, AgentRole.Painter)]
    [InlineData(VerificationRung.SchemaCoherence, AgentRole.Coder)]
    [InlineData(VerificationRung.Replay, AgentRole.Quant)]
    [InlineData(VerificationRung.Compile, AgentRole.Fixer)]
    [InlineData(VerificationRung.Policy, AgentRole.Fixer)]
    public void EachRungGoesToWhoeverIsClosestToIt(VerificationRung rung, AgentRole expected)
    {
        // A draw-probe failure is the Painter's own work coming back; a schema mismatch is the Coder
        // declaring a parameter it then hard-coded. Sending those to a general repair agent throws away
        // the context that makes them cheap.
        var state = new RoutingState(HasSpec: true, HasCode: true, FailedAt: rung);

        AgentRouter.Choose(state, Fresh)!.Role.Should().Be(expected);
    }

    // ── reliability modulates; it never overrides ───────────────────────────────────────────────

    [Fact]
    public void AnIneligibleAgentStaysUnreachableHoweverReliableItLooks()
    {
        // The invariant the whole design rests on. Painter before there is code is not a worse choice,
        // it is a meaningless one — and a bandit allowed to reach it would eventually try.
        var reliability = new AgentReliability();
        for (var i = 0; i < 50; i++) reliability.Record(AgentRole.Painter, succeeded: true);
        for (var i = 0; i < 50; i++) reliability.Record(AgentRole.Interviewer, succeeded: false);

        var weights = AgentRouter.Weigh(new RoutingState(), reliability);

        weights.Should().ContainSingle().Which.Key.Should().Be(AgentRole.Interviewer);
        weights.Should().NotContainKey(AgentRole.Painter);
    }

    [Fact]
    public void ReliabilityReordersAgentsThePriorThoughtComparable()
    {
        // Where the tilt is supposed to act: a rung whose prior splits between two agents.
        var state = new RoutingState(HasSpec: true, HasCode: true, FailedAt: VerificationRung.Replay);

        var quantWins = AgentRouter.Choose(state, Fresh)!.Role;
        quantWins.Should().Be(AgentRole.Quant, "the prior favours it 0.5 to 0.5 and ties break in order");

        var reliability = new AgentReliability();
        for (var i = 0; i < 20; i++) reliability.Record(AgentRole.Fixer, succeeded: true);
        for (var i = 0; i < 20; i++) reliability.Record(AgentRole.Quant, succeeded: false);

        AgentRouter.Choose(state, reliability)!.Role.Should().Be(AgentRole.Fixer);
    }

    [Fact]
    public void AtZeroEtaOnlyThePriorSpeaks()
    {
        var reliability = new AgentReliability();
        for (var i = 0; i < 20; i++) reliability.Record(AgentRole.Fixer, succeeded: true);

        var state = new RoutingState(HasSpec: true, HasCode: true, FailedAt: VerificationRung.DrawProbe);

        var weights = AgentRouter.Weigh(state, reliability, eta: 0d);
        weights[AgentRole.Painter].Should().BeApproximately(0.75d, 1e-9);
        weights[AgentRole.Fixer].Should().BeApproximately(0.25d, 1e-9);
    }

    [Fact]
    public void TheWeightsAreADistribution()
    {
        var state = new RoutingState(HasSpec: true, HasCode: true, FailedAt: VerificationRung.DrawProbe);

        var weights = AgentRouter.Weigh(state, Fresh);

        weights.Values.Sum().Should().BeApproximately(1d, 1e-9);
        weights.Values.Should().OnlyContain(w => w > 0d);
    }

    [Fact]
    public void TheDecisionCarriesTheArithmeticThatProducedIt()
    {
        // A trajectory log of bare agent names is a log nobody can distil later.
        var state = new RoutingState(HasSpec: true, HasCode: true, FailedAt: VerificationRung.DrawProbe);

        AgentRouter.Choose(state, Fresh)!.Weights.Should().HaveCount(2);
    }

    [Fact]
    public void ChoosingIsDeterministic()
    {
        // A user who re-runs a brief should get the same route. A session that wanders cannot be
        // debugged, and the exploration is worth far less than that over an action space of six.
        var state = new RoutingState(HasSpec: true, HasCode: true, FailedAt: VerificationRung.Replay);

        var first = AgentRouter.Choose(state, Fresh)!.Role;
        for (var i = 0; i < 20; i++)
            AgentRouter.Choose(state, Fresh)!.Role.Should().Be(first);
    }

    [Fact]
    public void SamplingIsReproducibleFromItsSeed()
    {
        // An exploration policy that cannot be replayed cannot be evaluated, which would leave the
        // trajectory log recording decisions nobody can account for.
        var state = new RoutingState(HasSpec: true, HasCode: true, FailedAt: VerificationRung.Replay);

        var a = Enumerable.Range(0, 30)
            .Select(_ => AgentRouter.Sample(state, Fresh, new Random(1234))!.Role).ToArray();
        var b = Enumerable.Range(0, 30)
            .Select(_ => AgentRouter.Sample(state, Fresh, new Random(1234))!.Role).ToArray();

        a.Should().Equal(b);
    }

    [Fact]
    public void SamplingReachesBothSidesOfASplitPrior()
    {
        var state = new RoutingState(HasSpec: true, HasCode: true, FailedAt: VerificationRung.DrawProbe);
        var random = new Random(7);

        var seen = Enumerable.Range(0, 200)
            .Select(_ => AgentRouter.Sample(state, Fresh, random)!.Role)
            .Distinct()
            .ToArray();

        seen.Should().BeEquivalentTo([AgentRole.Painter, AgentRole.Fixer]);
    }

    // ── the estimator ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnUnknownAgentStartsNeutralRatherThanAtZeroOrOne()
    {
        // Zero would multiply an agent out of contention permanently; one would have every newcomer
        // outrank proven peers. Neutral lets the prior decide until evidence exists.
        new AgentReliability().Of(AgentRole.Coder).Should().Be(AgentReliability.NeutralPrior);
        new AgentReliability().ObservationsFor(AgentRole.Coder).Should().Be(0);
    }

    [Fact]
    public void SuccessRaisesAndFailureLowers()
    {
        var reliability = new AgentReliability();

        reliability.Record(AgentRole.Coder, succeeded: true);
        reliability.Of(AgentRole.Coder).Should().BeGreaterThan(AgentReliability.NeutralPrior);

        var afterWin = reliability.Of(AgentRole.Coder);
        reliability.Record(AgentRole.Coder, succeeded: false);
        reliability.Of(AgentRole.Coder).Should().BeLessThan(afterWin);
    }

    [Fact]
    public void RecentEvidenceOutweighsOld()
    {
        // The point of an EMA here: the thing being measured is a model the user can change between
        // turns, so a long-run average would keep scoring the previous one.
        var reliability = new AgentReliability();
        for (var i = 0; i < 20; i++) reliability.Record(AgentRole.Coder, succeeded: false);
        for (var i = 0; i < 5; i++) reliability.Record(AgentRole.Coder, succeeded: true);

        reliability.Of(AgentRole.Coder).Should().BeGreaterThan(0.5d);
    }

    [Fact]
    public void PartialCreditIsPossible()
    {
        // Clearing six rungs of eight is not the same as failing to compile, and scoring both zero
        // throws away most of what the ladder measured.
        var reliability = new AgentReliability();
        reliability.Record(AgentRole.Coder, 0.75d);

        reliability.Of(AgentRole.Coder).Should().BeGreaterThan(AgentReliability.NeutralPrior);
        reliability.Of(AgentRole.Coder).Should().BeLessThan(1d);
    }

    [Fact]
    public void ScoresStayInRange()
    {
        var reliability = new AgentReliability();
        for (var i = 0; i < 100; i++) reliability.Record(AgentRole.Coder, succeeded: true);
        reliability.Of(AgentRole.Coder).Should().BeInRange(0d, 1d);

        for (var i = 0; i < 100; i++) reliability.Record(AgentRole.Coder, succeeded: false);
        reliability.Of(AgentRole.Coder).Should().BeInRange(0d, 1d);
    }

    [Fact]
    public void ObservationCountsDistinguishAnEstimateFromThePrior()
    {
        // 0.5 from evidence and 0.5 from having no evidence are different claims, and a router that
        // cannot tell them apart cannot report why it chose.
        var reliability = new AgentReliability();
        reliability.Record(AgentRole.Coder, 0.5d);

        reliability.Of(AgentRole.Coder).Should().Be(AgentReliability.NeutralPrior);
        reliability.ObservationsFor(AgentRole.Coder).Should().Be(1);
        reliability.ObservationsFor(AgentRole.Painter).Should().Be(0);
    }

    [Fact]
    public void AnAbsurdRewardIsRefusedRatherThanStored()
    {
        var act = () => new AgentReliability().Record(AgentRole.Coder, double.NaN);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
