using FluentAssertions;
using TradingTerminal.Infrastructure.Strategies.Authoring.Agents;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The join between the ladder and the router (#48) — the only place the two meet.
///
/// <para>Keeping it to one seam is what stops either side scoring itself: the router never reads a
/// report, and the ladder never learns an agent exists.</para>
/// </summary>
public sealed class LadderFeedbackTests
{
    private static VerificationReport Report(params VerificationStep[] steps) => new(steps);

    private static VerificationStep Pass(VerificationRung rung) => VerificationStep.Pass(rung);
    private static VerificationStep Skip(VerificationRung rung) => VerificationStep.Skip(rung);
    private static VerificationStep Fail(VerificationRung rung) =>
        VerificationStep.Fail(rung, new VerificationFinding("x.y", "wrong", "fix it"));

    [Fact]
    public void ClearingEverythingScoresNearlyFull()
    {
        var report = Report(
            Pass(VerificationRung.Compile), Pass(VerificationRung.Policy), Pass(VerificationRung.Shape),
            Pass(VerificationRung.SchemaCoherence), Pass(VerificationRung.Lifecycle),
            Pass(VerificationRung.DrawProbe), Pass(VerificationRung.Replay));

        LadderFeedback.RewardFor(report).Should().BeGreaterThanOrEqualTo(0.875d);
    }

    [Fact]
    public void GettingFurtherEarnsMore()
    {
        // Graded, not binary. Clearing six rungs and failing the seventh is a different contribution
        // from failing to compile, and scoring both zero throws away most of what the ladder measured.
        var nearMiss = Report(
            Pass(VerificationRung.Compile), Pass(VerificationRung.Policy), Pass(VerificationRung.Shape),
            Pass(VerificationRung.SchemaCoherence), Pass(VerificationRung.Lifecycle),
            Fail(VerificationRung.DrawProbe));

        var earlyFailure = Report(Fail(VerificationRung.Compile));

        LadderFeedback.RewardFor(nearMiss).Should().BeGreaterThan(LadderFeedback.RewardFor(earlyFailure));
        LadderFeedback.RewardFor(earlyFailure).Should().Be(0d);
    }

    [Fact]
    public void PassingBeatsAnyAmountOfPartialProgress()
    {
        var passed = Report(Pass(VerificationRung.Compile), Pass(VerificationRung.Shape));
        var almost = Report(
            Pass(VerificationRung.Compile), Pass(VerificationRung.Policy), Pass(VerificationRung.Shape),
            Pass(VerificationRung.SchemaCoherence), Pass(VerificationRung.Lifecycle),
            Pass(VerificationRung.DrawProbe), Fail(VerificationRung.Replay));

        LadderFeedback.RewardFor(passed).Should().BeGreaterThan(LadderFeedback.RewardFor(almost));
    }

    [Fact]
    public void SkippedRungsEarnNothing()
    {
        // Otherwise arranging to be checked by very little would look like clearing a lot, and that is
        // what agents would learn to produce.
        var mostlySkipped = Report(
            Pass(VerificationRung.Compile),
            Skip(VerificationRung.SchemaCoherence), Skip(VerificationRung.DrawProbe), Skip(VerificationRung.Replay));

        var actuallyChecked = Report(
            Pass(VerificationRung.Compile),
            Pass(VerificationRung.SchemaCoherence), Pass(VerificationRung.DrawProbe), Pass(VerificationRung.Replay));

        LadderFeedback.RewardFor(mostlySkipped).Should().BeLessThan(LadderFeedback.RewardFor(actuallyChecked));
    }

    [Fact]
    public void AnEmptyReportEarnsNothing()
    {
        LadderFeedback.RewardFor(Report()).Should().Be(0d);
    }

    [Fact]
    public void TheDenominatorIsFixedSoAShortRunCannotFlatterItself()
    {
        // Taken from the report, a run that stopped after one rung would score 1/1 and look perfect.
        var stoppedEarly = Report(Pass(VerificationRung.Compile));
        var wentAllTheWay = Report(
            Pass(VerificationRung.Compile), Pass(VerificationRung.Policy), Pass(VerificationRung.Shape),
            Pass(VerificationRung.SchemaCoherence), Pass(VerificationRung.Lifecycle),
            Pass(VerificationRung.DrawProbe), Pass(VerificationRung.Replay));

        LadderFeedback.RewardFor(stoppedEarly).Should().BeLessThan(LadderFeedback.RewardFor(wentAllTheWay));
        LadderFeedback.RewardFor(stoppedEarly).Should().BeLessThan(0.6d);
    }

    // ── advancing the state ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFailureIsCarriedIntoTheStateSoRoutingSeesIt()
    {
        var state = LadderFeedback.Advance(
            new RoutingState(HasSpec: true, MustDraw: true),
            Report(Pass(VerificationRung.Compile), Fail(VerificationRung.DrawProbe)));

        state.FailedAt.Should().Be(VerificationRung.DrawProbe);
        AgentRouter.Choose(state, new AgentReliability())!.Role.Should().Be(AgentRole.Painter);
    }

    [Fact]
    public void WhatTheReportCannotKnowIsCarriedThroughRatherThanReset()
    {
        // A report says nothing about whether a brief became a spec or whether a human reviewed the
        // result. Inferring them would make a fresh verification look like a fresh session and send the
        // loop back to the Interviewer.
        var before = new RoutingState(HasSpec: true, NeedsMaths: true, MustDraw: true, Reviewed: true);

        var after = LadderFeedback.Advance(before, Report(Pass(VerificationRung.Compile)));

        after.HasSpec.Should().BeTrue();
        after.NeedsMaths.Should().BeTrue();
        after.MustDraw.Should().BeTrue();
        after.Reviewed.Should().BeTrue();
    }

    [Fact]
    public void ADrawPassIsRemembered()
    {
        var state = LadderFeedback.Advance(
            new RoutingState(HasSpec: true, MustDraw: true),
            Report(Pass(VerificationRung.Compile), Pass(VerificationRung.DrawProbe)));

        state.Draws.Should().BeTrue();
        state.Compiles.Should().BeTrue();
    }

    [Fact]
    public void AFailedCompileIsNotRecordedAsCompiling()
    {
        LadderFeedback.Advance(new RoutingState(HasSpec: true), Report(Fail(VerificationRung.Compile)))
            .Compiles.Should().BeFalse();
    }

    [Fact]
    public void RecordingRoutesTheVerdictToTheAgentThatProducedIt()
    {
        var reliability = new AgentReliability();

        LadderFeedback.Record(reliability, AgentRole.Coder, Report(Fail(VerificationRung.Compile)));

        reliability.Of(AgentRole.Coder).Should().BeLessThan(AgentReliability.NeutralPrior);
        reliability.Of(AgentRole.Painter).Should().Be(AgentReliability.NeutralPrior);
    }
}
