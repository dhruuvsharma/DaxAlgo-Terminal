using FluentAssertions;
using TradingTerminal.Core.Quant;
using Xunit;

namespace TradingTerminal.Tests.Quant;

public sealed class SignalWeightsTests
{
    private static double L1(double[] w) => w.Sum(Math.Abs);

    [Fact]
    public void Weights_AreL1Normalised()
    {
        var cov = new double[,]
        {
            { 1.0, 0.2, 0.0 },
            { 0.2, 1.0, 0.1 },
            { 0.0, 0.1, 1.0 },
        };
        var ic = new[] { 0.05, 0.03, -0.02 };

        var w = SignalWeights.Solve(cov, ic);

        L1(w).Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void UncorrelatedSignals_WeightsProportionalToIc()
    {
        // Identity covariance ⇒ w ∝ Σ⁻¹·IC = IC, then L1 normalised ⇒ w = IC / Σ|IC|.
        var cov = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        var ic = new[] { 0.04, 0.02, 0.06 };

        var w = SignalWeights.Solve(cov, ic);

        var total = ic.Sum(Math.Abs);
        w[0].Should().BeApproximately(0.04 / total, 1e-9);
        w[1].Should().BeApproximately(0.02 / total, 1e-9);
        w[2].Should().BeApproximately(0.06 / total, 1e-9);
        // Proportionality: the ratio of weights equals the ratio of ICs.
        (w[2] / w[1]).Should().BeApproximately(0.06 / 0.02, 1e-9);
    }

    [Fact]
    public void NegativeIc_YieldsNegativeHedgeWeight()
    {
        var cov = new double[,] { { 1, 0 }, { 0, 1 } };
        var ic = new[] { 0.05, -0.03 };

        var w = SignalWeights.Solve(cov, ic);

        w[0].Should().BeGreaterThan(0);
        w[1].Should().BeLessThan(0); // sign preserved → hedge weight
    }

    [Fact]
    public void PerfectlyCorrelatedDuplicates_SplitWeight_RatherThanDoubleCount()
    {
        // Compare: a single signal with IC=0.05 paired with one independent signal IC=0.05,
        // versus TWO perfectly correlated copies of that signal (IC=0.05 each) plus the same
        // independent one. The two duplicates should *share* the exposure a single signal would
        // command, not double-count it.
        var icPair = new[] { 0.05, 0.05 };
        var covPair = new double[,] { { 1, 0 }, { 0, 1 } }; // independent
        var wPair = SignalWeights.Solve(covPair, icPair);

        var icDup = new[] { 0.05, 0.05, 0.05 };
        // Signals 0 and 1 are (near-)perfectly correlated; signal 2 is independent.
        const double rho = 0.999;
        var covDup = new double[,]
        {
            { 1.0, rho, 0.0 },
            { rho, 1.0, 0.0 },
            { 0.0, 0.0, 1.0 },
        };
        var wDup = SignalWeights.Solve(covDup, icDup);

        // The combined weight of the two duplicates should be close to the weight a single
        // independent signal of equal IC carries (≈ what signal 2 gets), not twice it.
        var dupCombined = Math.Abs(wDup[0]) + Math.Abs(wDup[1]);
        var independentWeight = Math.Abs(wDup[2]);

        // Each duplicate individually carries far less than the independent signal.
        Math.Abs(wDup[0]).Should().BeLessThan(independentWeight);
        // And the duplicate pair together is comparable to a single independent signal, NOT double.
        dupCombined.Should().BeLessThan(independentWeight * 1.5);

        // Sanity: in the fully independent equal-IC pair the two weights are equal.
        wPair[0].Should().BeApproximately(wPair[1], 1e-9);
    }

    [Fact]
    public void ZeroIc_FallsBackToEqualWeights()
    {
        var cov = new double[,] { { 1, 0 }, { 0, 1 } };
        var ic = new[] { 0.0, 0.0 };

        var w = SignalWeights.Solve(cov, ic);

        L1(w).Should().BeApproximately(1.0, 1e-9);
        w[0].Should().BeApproximately(0.5, 1e-9);
        w[1].Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        SignalWeights.Solve(new double[0, 0], Array.Empty<double>()).Should().BeEmpty();
    }

    [Fact]
    public void DimensionMismatch_Throws()
    {
        var cov = new double[,] { { 1, 0 }, { 0, 1 } };
        var act = () => SignalWeights.Solve(cov, new[] { 0.1 });
        act.Should().Throw<ArgumentException>();
    }
}
