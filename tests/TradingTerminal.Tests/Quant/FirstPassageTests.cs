using FluentAssertions;
using TradingTerminal.Core.Quant;
using Xunit;

namespace TradingTerminal.Tests.Quant;

public sealed class FirstPassageTests
{
    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(1.0, 3.0)]
    [InlineData(0.5, 2.5)]
    public void ZeroDrift_ProbabilityIsAOverAPlusB(double a, double b)
    {
        var p = FirstPassage.WinProbability(stop: a, target: b, mu: 0.0, sigma: 1.0);
        p.Should().BeApproximately(a / (a + b), 1e-12);
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(1.0, 3.0)]
    public void ZeroDrift_ExpectedValueBeforeCosts_IsZero(double a, double b)
    {
        // EV = P·b − (1−P)·a with P = a/(a+b) collapses to 0 — the spec's EV-zero guard.
        var p = FirstPassage.WinProbability(stop: a, target: b, mu: 0.0, sigma: 1.0);
        var ev = FirstPassage.ExpectedValue(p, target: b, stop: a, roundTripCosts: 0.0);
        ev.Should().BeApproximately(0.0, 1e-12);
    }

    [Fact]
    public void PositiveDrift_RaisesProbability_NegativeLowersIt()
    {
        const double a = 1.0, b = 1.0, sigma = 1.0;
        var driftless = a / (a + b); // 0.5

        var up = FirstPassage.WinProbability(a, b, mu: 0.3, sigma);
        var down = FirstPassage.WinProbability(a, b, mu: -0.3, sigma);

        up.Should().BeGreaterThan(driftless);
        down.Should().BeLessThan(driftless);
        // Symmetric barriers ⇒ symmetric probabilities about 0.5.
        (up + down).Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void ClosedForm_MatchesHandComputedFormula()
    {
        // P = (e^{θa} − 1) / (e^{θa} − e^{−θb}), θ = 2μ/σ².
        const double a = 2.0, b = 3.0, mu = 0.1, sigma = 1.0;
        var theta = 2.0 * mu / (sigma * sigma);
        var expected = (Math.Exp(theta * a) - 1.0) / (Math.Exp(theta * a) - Math.Exp(-theta * b));

        var p = FirstPassage.WinProbability(a, b, mu, sigma);

        p.Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void LargeDrift_DoesNotOverflow_AndSaturates()
    {
        // Very large positive μ·a/σ² ⇒ P → 1; large negative ⇒ P → 0; both finite.
        var pHigh = FirstPassage.WinProbability(stop: 50, target: 50, mu: 100.0, sigma: 0.1);
        var pLow = FirstPassage.WinProbability(stop: 50, target: 50, mu: -100.0, sigma: 0.1);

        pHigh.Should().BeInRange(0.0, 1.0);
        pLow.Should().BeInRange(0.0, 1.0);
        pHigh.Should().BeApproximately(1.0, 1e-9);
        pLow.Should().BeApproximately(0.0, 1e-9);
        double.IsNaN(pHigh).Should().BeFalse();
        double.IsInfinity(pHigh).Should().BeFalse();
    }

    [Fact]
    public void DegenerateBarriers_AreHandled()
    {
        FirstPassage.WinProbability(stop: 0, target: 1, mu: 0, sigma: 1).Should().Be(0.0); // no room to stop
        FirstPassage.WinProbability(stop: 1, target: 0, mu: 0, sigma: 1).Should().Be(1.0); // no room to target
        FirstPassage.WinProbability(stop: 0, target: 0, mu: 0, sigma: 1).Should().Be(0.5);
    }

    [Fact]
    public void ZeroSigma_IsDeterministicByDriftSign()
    {
        FirstPassage.WinProbability(stop: 1, target: 1, mu: 0.5, sigma: 0).Should().Be(1.0);
        FirstPassage.WinProbability(stop: 1, target: 1, mu: -0.5, sigma: 0).Should().Be(0.0);
    }

    [Fact]
    public void GapPenalty_DeductsFromWinProbability_AndClamps()
    {
        // Driftless symmetric barriers ⇒ base P = 0.5. A 0.2 gap penalty ⇒ 0.3.
        var penalised = FirstPassage.WinProbability(stop: 1, target: 1, mu: 0, sigma: 1, gapPenalty: 0.2);
        penalised.Should().BeApproximately(0.3, 1e-12);

        // The 4-arg call (no penalty) is unaffected.
        var basep = FirstPassage.WinProbability(stop: 1, target: 1, mu: 0, sigma: 1);
        basep.Should().BeApproximately(0.5, 1e-12);

        // A penalty larger than the base probability clamps at 0, never negative.
        var floored = FirstPassage.WinProbability(stop: 1, target: 1, mu: 0, sigma: 1, gapPenalty: 0.9);
        floored.Should().Be(0.0);
    }

    [Fact]
    public void ExpectedValue_SubtractsRoundTripCosts()
    {
        // P=0.6, b=2, a=1: gross EV = 0.6*2 - 0.4*1 = 1.2 - 0.4 = 0.8. With costs 0.3 ⇒ 0.5.
        var gross = FirstPassage.ExpectedValue(0.6, target: 2, stop: 1, roundTripCosts: 0.0);
        var net = FirstPassage.ExpectedValue(0.6, target: 2, stop: 1, roundTripCosts: 0.3);

        gross.Should().BeApproximately(0.8, 1e-12);
        net.Should().BeApproximately(0.5, 1e-12);
        (gross - net).Should().BeApproximately(0.3, 1e-12);
    }
}
