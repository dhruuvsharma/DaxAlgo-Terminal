using FluentAssertions;
using TradingTerminal.Core.Quant;
using Xunit;

namespace TradingTerminal.Tests.Quant;

public sealed class KyleResidualTests
{
    [Fact]
    public void RecoversKnownLambda_FromSyntheticImpact()
    {
        var rng = new Random(4242);
        const double lambda = 0.005;
        var n = 500;
        var flow = new double[n];
        var ret = new double[n];
        for (var i = 0; i < n; i++)
        {
            flow[i] = rng.NextGaussian() * 100.0; // signed order flow
            ret[i] = lambda * flow[i] + rng.NextGaussian() * 0.01; // small noise
        }

        var fit = KyleResidual.Fit(ret, flow);

        fit.Lambda.Should().BeApproximately(lambda, 5e-4);
        fit.RSquared.Should().BeGreaterThan(0.8);
        fit.Count.Should().Be(n);
    }

    [Fact]
    public void PureNoiseFlow_YieldsNearZeroLambda_AndLowR2()
    {
        var rng = new Random(8);
        var n = 600;
        var flow = new double[n];
        var ret = new double[n];
        for (var i = 0; i < n; i++)
        {
            flow[i] = rng.NextGaussian();   // flow independent of returns
            ret[i] = rng.NextGaussian();    // returns independent of flow
        }

        var fit = KyleResidual.Fit(ret, flow);

        fit.Lambda.Should().BeApproximately(0.0, 0.1);
        fit.RSquared.Should().BeLessThan(0.05);
    }

    [Fact]
    public void ZeroResidualFit_HasZeroCumulativeResidual()
    {
        // r = 0.5·Δ exactly ⇒ residuals all zero ⇒ ε_cum = 0.
        var flow = new double[] { -3, -1, 0, 2, 4, 6 };
        var ret = flow.Select(f => 0.5 * f).ToArray();

        var fit = KyleResidual.Fit(ret, flow);

        fit.Lambda.Should().BeApproximately(0.5, 1e-9);
        fit.CumulativeResidual.Should().BeApproximately(0.0, 1e-9);
        fit.Residuals.Should().OnlyContain(e => Math.Abs(e) < 1e-9);
        fit.CumulativeResidualZ.Should().Be(0.0);
    }

    [Fact]
    public void CumulativeResidualZ_RespondsToInjectedDrift()
    {
        var rng = new Random(77);
        var n = 200;
        var flow = new double[n];
        var ret = new double[n];
        const double lambda = 0.01;
        for (var i = 0; i < n; i++)
        {
            flow[i] = rng.NextGaussian() * 50.0;
            ret[i] = lambda * flow[i] + rng.NextGaussian() * 0.02;
            // Inject a positive drift in the last quarter that is NOT explained by flow.
            if (i >= 3 * n / 4) ret[i] += 0.15;
        }

        var fit = KyleResidual.Fit(ret, flow);

        // The unexplained drift accumulates a large positive cumulative residual, far above the
        // path's mean ⇒ a strongly positive z.
        fit.CumulativeResidual.Should().BeGreaterThan(0);
        fit.CumulativeResidualZ.Should().BeGreaterThan(1.0);
    }

    [Fact]
    public void TooFewObservations_ReturnsDegenerateResult()
    {
        var fit = KyleResidual.Fit(new double[] { 0.1 }, new double[] { 5 });
        fit.Lambda.Should().Be(0);
        fit.Count.Should().Be(1);
        fit.Residuals.Should().HaveCount(1);
    }
}
