using FluentAssertions;
using TradingTerminal.Core.Quant;
using Xunit;

namespace TradingTerminal.Tests.Quant;

/// <summary>
/// Covers the 2SLS instrumental-variable Kyle-λ estimator. The instrument is the one-bar-lagged
/// flow Δ_{t−1}; it is only valid (relevant) when flow is autocorrelated, so the recovery tests
/// generate AR(1) flow — the realistic case (aggressor flow persists).
/// </summary>
public sealed class KyleResidualTests
{
    /// <summary>Generates AR(1) signed flow Δ_t = φ·Δ_{t−1} + u_t so the lagged instrument is strong.</summary>
    private static double[] Ar1Flow(Random rng, int n, double phi, double scale)
    {
        var flow = new double[n];
        double prev = 0;
        for (var i = 0; i < n; i++)
        {
            prev = phi * prev + rng.NextGaussian() * scale;
            flow[i] = prev;
        }
        return flow;
    }

    [Fact]
    public void RecoversKnownLambda_FromSyntheticImpact_WithValidInstrument()
    {
        var rng = new Random(4242);
        const double lambda = 0.005;
        var n = 1500;
        var flow = Ar1Flow(rng, n, phi: 0.6, scale: 100.0);
        var ret = new double[n];
        for (var i = 0; i < n; i++)
            ret[i] = lambda * flow[i] + rng.NextGaussian() * 0.01;

        var fit = KyleResidual.Fit(ret, flow);

        fit.Lambda.Should().BeApproximately(lambda, 5e-4);
        fit.RSquared.Should().BeGreaterThan(0.7);
        // The instrument is strong because flow is autocorrelated.
        fit.FirstStageRSquared.Should().BeGreaterThan(0.1);
        fit.Count.Should().Be(n - 1);
    }

    [Fact]
    public void FlowUnrelatedToReturns_YieldsNearZeroLambda()
    {
        var rng = new Random(8);
        var n = 1500;
        // Strong (autocorrelated) instrument, but returns are independent of flow ⇒ λ ≈ 0.
        var flow = Ar1Flow(rng, n, phi: 0.6, scale: 50.0);
        var ret = new double[n];
        for (var i = 0; i < n; i++) ret[i] = rng.NextGaussian() * 0.01;

        var fit = KyleResidual.Fit(ret, flow);

        fit.Lambda.Should().BeApproximately(0.0, 1e-3);
        fit.RSquared.Should().BeLessThan(0.05);
    }

    [Fact]
    public void ExactLinearImpact_HasZeroCumulativeResidual()
    {
        // r = 0.5·Δ exactly ⇒ 2SLS recovers λ = 0.5 (Cov(y,ŷ)=Var(ŷ)) and the residuals vanish.
        var rng = new Random(13);
        var flow = Ar1Flow(rng, 60, phi: 0.5, scale: 10.0);
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
        var n = 400;
        var flow = Ar1Flow(rng, n, phi: 0.6, scale: 50.0);
        var ret = new double[n];
        const double lambda = 0.01;
        for (var i = 0; i < n; i++)
        {
            ret[i] = lambda * flow[i] + rng.NextGaussian() * 0.02;
            // Inject a positive drift in the last quarter that is NOT explained by flow.
            if (i >= 3 * n / 4) ret[i] += 0.15;
        }

        var fit = KyleResidual.Fit(ret, flow);

        // Σε is ~0 by construction (the fitted intercept absorbs the mean), so the drift shows up in
        // the z-score: the cumulative path troughs then climbs back near 0, leaving the final point
        // well above the path mean.
        fit.CumulativeResidualZ.Should().BeGreaterThan(1.0);
    }

    [Fact]
    public void TooFewObservations_ReturnsDegenerateResult()
    {
        // One observation cannot form a lagged instrument ⇒ effective sample 0.
        var fit = KyleResidual.Fit(new double[] { 0.1 }, new double[] { 5 });
        fit.Lambda.Should().Be(0);
        fit.Count.Should().Be(0);
        fit.Residuals.Should().BeEmpty();
    }
}
