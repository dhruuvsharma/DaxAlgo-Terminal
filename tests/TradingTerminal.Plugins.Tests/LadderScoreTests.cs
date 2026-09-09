using FluentAssertions;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Turning a ladder verdict into a number — the two numbers the swarm runs on.
///
/// <para><see cref="LadderScore.RewardFor"/> is what the trajectory log records, so a turn's cost can
/// be set against what it bought. <see cref="LadderScore.HeightOf"/> is what the stall detector
/// compares, so a run that has stopped making ground stops rather than spending the rest of its
/// budget on the same wall.</para>
///
/// <para>Both live beside the ladder rather than beside the agents. As <c>LadderFeedback</c> this file
/// also advanced a routing state, which meant scoring a report required knowing what an agent was.
/// The router is gone; scoring is not.</para>
/// </summary>
public sealed class LadderScoreTests
{
    private static VerificationReport Report(params VerificationStep[] steps) => new(steps);

    private static VerificationStep Pass(VerificationRung rung) => VerificationStep.Pass(rung);
    private static VerificationStep Skip(VerificationRung rung) => VerificationStep.Skip(rung);
    private static VerificationStep Fail(VerificationRung rung) =>
        VerificationStep.Fail(rung, new VerificationFinding("x.y", "wrong", "fix it"));

    private static VerificationStep Fail(VerificationRung rung, params string[] codes) =>
        VerificationStep.Fail(rung, [.. codes.Select(c => new VerificationFinding(c, "wrong", "fix it"))]);

    [Fact]
    public void ClearingEverythingScoresNearlyFull()
    {
        var report = Report(
            Pass(VerificationRung.Compile), Pass(VerificationRung.Policy), Pass(VerificationRung.Shape),
            Pass(VerificationRung.SchemaCoherence), Pass(VerificationRung.Lifecycle),
            Pass(VerificationRung.DrawProbe), Pass(VerificationRung.Replay));

        LadderScore.RewardFor(report).Should().BeGreaterThanOrEqualTo(0.875d);
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

        LadderScore.RewardFor(nearMiss).Should().BeGreaterThan(LadderScore.RewardFor(earlyFailure));
        LadderScore.RewardFor(earlyFailure).Should().Be(0d);
    }

    [Fact]
    public void PassingBeatsAnyAmountOfPartialProgress()
    {
        var passed = Report(Pass(VerificationRung.Compile), Pass(VerificationRung.Shape));
        var almost = Report(
            Pass(VerificationRung.Compile), Pass(VerificationRung.Policy), Pass(VerificationRung.Shape),
            Pass(VerificationRung.SchemaCoherence), Pass(VerificationRung.Lifecycle),
            Pass(VerificationRung.DrawProbe), Fail(VerificationRung.Replay));

        LadderScore.RewardFor(passed).Should().BeGreaterThan(LadderScore.RewardFor(almost));
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

        LadderScore.RewardFor(mostlySkipped).Should().BeLessThan(LadderScore.RewardFor(actuallyChecked));
    }

    [Fact]
    public void AnEmptyReportEarnsNothing()
    {
        LadderScore.RewardFor(Report()).Should().Be(0d);
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

        LadderScore.RewardFor(stoppedEarly).Should().BeLessThan(LadderScore.RewardFor(wentAllTheWay));
        LadderScore.RewardFor(stoppedEarly).Should().BeLessThan(0.6d);
    }

    // ── height, the swarm's definition of progress ──────────────────────────────────────────────

    [Fact]
    public void ClearingARungBeatsAnyNumberOfFindingsRemoved()
    {
        // Rungs dominate findings by a margin no realistic finding count can close, so a repair that
        // gets one rung further has always bought more ground than one that merely tidied diagnostics.
        var higher = Report(Pass(VerificationRung.Compile), Pass(VerificationRung.Policy),
                            Fail(VerificationRung.Shape, "a", "b", "c", "d", "e"));
        var lower = Report(Pass(VerificationRung.Compile), Fail(VerificationRung.Policy));

        LadderScore.HeightOf(higher).Should().BeGreaterThan(LadderScore.HeightOf(lower));
    }

    [Fact]
    public void RemovingFindingsAtTheSameHeightIsStillProgress()
    {
        // Four errors becoming three IS progress and must keep its budget. A stall detector that
        // could not see this stopped converging runs at the third round.
        var fewer = Report(Pass(VerificationRung.Compile), Fail(VerificationRung.Shape, "a"));
        var more = Report(Pass(VerificationRung.Compile), Fail(VerificationRung.Shape, "a", "b", "c"));

        LadderScore.HeightOf(fewer).Should().BeGreaterThan(LadderScore.HeightOf(more));
    }

    [Fact]
    public void AVerdictThatClearsNothingHasNegativeHeight()
    {
        // Which is why a run seeds its best-so-far at int.MinValue rather than at -1: seeding at -1
        // made genuine early progress read as none.
        LadderScore.HeightOf(Report(Fail(VerificationRung.Compile, "a", "b")))
            .Should().BeNegative();
    }
}
