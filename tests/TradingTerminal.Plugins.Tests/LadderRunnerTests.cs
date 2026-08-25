using FluentAssertions;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Composing the rungs (#46) — order, short-circuiting, and what a report is allowed to claim.
/// </summary>
public sealed class LadderRunnerTests
{
    private static Func<VerificationStep> Pass(VerificationRung rung) => () => VerificationStep.Pass(rung);

    private static Func<VerificationStep> Skip(VerificationRung rung) => () => VerificationStep.Skip(rung);

    private static Func<VerificationStep> Fail(VerificationRung rung) =>
        () => VerificationStep.Fail(rung, new VerificationFinding("x.y", "wrong", "fix it"));

    [Fact]
    public void EveryRungRunsWhenNothingFails()
    {
        var report = LadderRunner.Run(
            Pass(VerificationRung.Compile),
            Pass(VerificationRung.SchemaCoherence),
            Pass(VerificationRung.DrawProbe));

        report.Passed.Should().BeTrue();
        report.RungsCleared.Should().Be(3);
        report.FailedAt.Should().BeNull();
    }

    [Fact]
    public void NothingRunsAfterAFailure()
    {
        // Not an optimisation. A unit that fails to compile also fails to instantiate, draw and trade;
        // running those describes one fault four times.
        var laterRan = false;

        var report = LadderRunner.Run(
            Pass(VerificationRung.Compile),
            Fail(VerificationRung.SchemaCoherence),
            () => { laterRan = true; return VerificationStep.Pass(VerificationRung.DrawProbe); });

        laterRan.Should().BeFalse();
        report.Steps.Should().HaveCount(2);
        report.FailedAt.Should().Be(VerificationRung.SchemaCoherence);
    }

    [Fact]
    public void ASkippedRungDoesNotStopTheLadderAndDoesNotCountAsCleared()
    {
        // The distinction the whole report rests on: a rung that did not apply is not a rung that was
        // passed, and reward must not treat them alike.
        var report = LadderRunner.Run(
            Pass(VerificationRung.Compile),
            Skip(VerificationRung.DrawProbe),
            Pass(VerificationRung.Replay));

        report.Passed.Should().BeTrue();
        report.Steps.Should().HaveCount(3);
        report.RungsCleared.Should().Be(2, "the skipped rung earned nothing");
    }

    [Fact]
    public void AnEmptyReportIsNotAPass()
    {
        // Verifying nothing must never read as verified. This is the cheapest possible reward hack and
        // it has to be closed at the type level rather than by convention.
        new VerificationReport([]).Passed.Should().BeFalse();
        LadderRunner.Run().Passed.Should().BeFalse();
    }

    [Fact]
    public void AReportOfNothingButSkipsIsNotAPass()
    {
        // The same hack, one step subtler: arrange that every rung skips, then claim success. Closed at
        // the type level rather than by asking callers to remember to check RungsCleared, because a
        // convention that has to be remembered is a convention that will be forgotten.
        var report = LadderRunner.Run(Skip(VerificationRung.DrawProbe), Skip(VerificationRung.Replay));

        report.RungsCleared.Should().Be(0);
        report.Passed.Should().BeFalse("nothing was actually checked");
    }

    [Fact]
    public void TheEarliestFailureIsTheOneReported()
    {
        LadderRunner.Run(Fail(VerificationRung.Compile), Fail(VerificationRung.Replay))
            .FailedAt.Should().Be(VerificationRung.Compile);
    }

    [Fact]
    public void FindingsAreGatheredAcrossEveryRungThatRan()
    {
        LadderRunner.Run(Pass(VerificationRung.Compile), Fail(VerificationRung.DrawProbe))
            .Findings.Should().ContainSingle().Which.Code.Should().Be("x.y");
    }

    // ── a probe that is itself broken ────────────────────────────────────────────────────────────

    [Fact]
    public void AProbeThatThrowsIsReportedAsTheVerifiersFaultRatherThanTheCandidates()
    {
        // Letting it escape would take down the build that was verifying somebody's strategy and say
        // nothing about the strategy. The candidate has not been judged, and the finding says so.
        var report = LadderRunner.RunGuarded(
            (VerificationRung.Compile, Pass(VerificationRung.Compile)),
            (VerificationRung.DrawProbe, () => throw new InvalidOperationException("bad probe")));

        report.Passed.Should().BeFalse();
        var finding = report.Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("ladder.probe-faulted");
        finding.Remedy.Should().Contain("not been judged");
    }

    [Fact]
    public void AFaultedProbeStopsTheLadder()
    {
        var laterRan = false;

        LadderRunner.RunGuarded(
            (VerificationRung.Compile, () => throw new InvalidOperationException("bad probe")),
            (VerificationRung.DrawProbe, () => { laterRan = true; return VerificationStep.Pass(VerificationRung.DrawProbe); }));

        laterRan.Should().BeFalse();
    }
}
