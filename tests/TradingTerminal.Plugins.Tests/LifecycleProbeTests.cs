using FluentAssertions;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Rung 6 of the verification ladder (#46) — does the unit survive being driven?
/// </summary>
public sealed class LifecycleProbeTests
{
    [Fact]
    public void AUnitThatSurvivesTheDrivePasses()
    {
        LifecycleProbe.Run(() => { }).Outcome.Should().Be(VerificationOutcome.Passed);
    }

    [Fact]
    public void AThrowDuringTheDriveIsReportedWithItsPhase()
    {
        // The probe cannot see inside the delegate, so the caller names the phase. "It threw" alone
        // makes a repair agent read the whole file to find out where.
        var step = LifecycleProbe.Run(
            () => throw new InvalidOperationException("boom"),
            phase: "OnStartAsync");

        step.Outcome.Should().Be(VerificationOutcome.Failed);
        var finding = step.Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("lifecycle.threw");
        finding.Message.Should().Contain("OnStartAsync").And.Contain("boom");
    }

    [Theory]
    [InlineData(typeof(ArgumentOutOfRangeException), "warm-up")]
    [InlineData(typeof(IndexOutOfRangeException), "warm-up")]
    [InlineData(typeof(KeyNotFoundException), "schema does not declare")]
    [InlineData(typeof(DivideByZeroException), "still zero")]
    [InlineData(typeof(NullReferenceException), "OnStartAsync")]
    public void TheRemedyNamesTheMistakeRatherThanTheException(Type exception, string expected)
    {
        // These five are almost always the same five mistakes. Naming the mistake instead of restating
        // the exception saves a repair round trip, which the user pays for.
        var step = LifecycleProbe.Run(() => throw (Exception)Activator.CreateInstance(exception)!);

        step.Findings.Should().ContainSingle().Which.Remedy.Should().Contain(expected);
    }

    [Fact]
    public void AnUnrecognisedFailureStillGetsAUsefulRemedy()
    {
        LifecycleProbe.Run(() => throw new FormatException("odd"))
            .Findings.Should().ContainSingle().Which.Remedy.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TheBudgetIsGenerousEnoughThatNoHonestUnitMeetsIt()
    {
        // A liveness check, not a performance one. A tight budget here would fail correct units on a
        // busy machine, which is the false positive that poisons the reward signal.
        LifecycleProbe.Budget.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(5));
    }
}
