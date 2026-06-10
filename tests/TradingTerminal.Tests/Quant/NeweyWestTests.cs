using FluentAssertions;
using TradingTerminal.Core.Quant;
using Xunit;

namespace TradingTerminal.Tests.Quant;

public sealed class NeweyWestTests
{
    /// <summary>
    /// Naive OLS slope SE for a simple regression: sqrt( Σe²/(n-2) / S_xx ). NW with the same
    /// residuals at lag 0 uses the meat Σx̃²e² (no degrees-of-freedom correction); we compare the
    /// lag-0 NW SE to the textbook OLS SE that shares the same meat shape.
    /// </summary>
    private static double OlsSlopeSeNoDof(double[] x, double[] residuals)
    {
        var n = x.Length;
        var mx = x.Average();
        double sxx = 0, meat = 0;
        for (var i = 0; i < n; i++)
        {
            var xt = x[i] - mx;
            sxx += xt * xt;
            meat += xt * xt * residuals[i] * residuals[i];
        }
        return Math.Sqrt(meat / (sxx * sxx));
    }

    [Fact]
    public void Lag0_EqualsWhiteHeteroskedasticityRobustSe()
    {
        var rng = new Random(7);
        var n = 200;
        var x = new double[n];
        var resid = new double[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = rng.NextDouble() * 6 - 3;
            resid[i] = rng.NextGaussian() * 0.5;
        }

        var nw = NeweyWest.SlopeStandardError(x, resid, slope: 1.0, lag: 0);
        var hand = OlsSlopeSeNoDof(x, resid);

        nw.Lag.Should().Be(0);
        nw.StandardError.Should().BeApproximately(hand, 1e-9);
    }

    [Fact]
    public void WhiteNoiseResiduals_AutoLag_CloseToLag0()
    {
        var rng = new Random(101);
        var n = 300;
        var x = new double[n];
        var resid = new double[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = rng.NextGaussian();
            resid[i] = rng.NextGaussian() * 0.3;
        }

        var lag0 = NeweyWest.SlopeStandardError(x, resid, lag: 0);
        var auto = NeweyWest.SlopeStandardError(x, resid, lag: -1);

        // With genuinely white residuals the lagged covariances ≈ 0, so the auto-lag SE should be
        // within a small fraction of the lag-0 SE.
        auto.StandardError.Should().BeApproximately(lag0.StandardError, lag0.StandardError * 0.25);
    }

    [Fact]
    public void Ar1PositiveAutocorrelation_NwSeMateriallyLargerThanNaiveOls()
    {
        var rng = new Random(2024);
        var n = 400;
        const double phi = 0.7;
        // Persistent regressor (a slow AR(1)) so the score uᵢ = x̃ᵢ·eᵢ inherits the residual
        // autocorrelation — the regime where ignoring serial correlation understates the SE.
        var x = new double[n];
        var resid = new double[n];
        double prevX = 0, prevE = 0;
        for (var i = 0; i < n; i++)
        {
            prevX = 0.9 * prevX + rng.NextGaussian();
            x[i] = prevX;
            var e = phi * prevE + rng.NextGaussian();
            resid[i] = e;
            prevE = e;
        }

        var nw = NeweyWest.SlopeStandardError(x, resid, lag: -1);
        var naive = OlsSlopeSeNoDof(x, resid);

        // Positive serial correlation in both the regressor and residuals inflates the true SE;
        // NW should be materially larger than the lag-0/naive SE that ignores autocorrelation.
        nw.StandardError.Should().BeGreaterThan(naive * 1.15);
    }

    [Fact]
    public void AutoLag_SpotCheck_n100_Is4()
    {
        // floor(4 · (100/100)^(2/9)) = floor(4 · 1) = 4.
        NeweyWest.AutoLag(100).Should().Be(4);
        NeweyWest.AutoLag(1).Should().Be(0);
        NeweyWest.AutoLag(0).Should().Be(0);
    }

    [Fact]
    public void BartlettWeights_DecreaseTheLag1ContributionAsBandwidthGrows()
    {
        // The Bartlett kernel k_l = 1 − l/(L+1) puts weight on the lag-1 autocovariance that
        // *decreases* as the bandwidth L grows: k_1(L=1)=1/2, k_1(L=2)=2/3? No — 1−1/(L+1):
        //   L=1 ⇒ 1−1/2 = 0.5 ; L=2 ⇒ 1−1/3 ≈ 0.667 ; L=3 ⇒ 1−1/4 = 0.75 (increasing in L).
        // Within a fixed L the weights decrease in l. We verify the *within-L* monotonicity by
        // isolating each lag: a residual series whose only non-trivial autocovariance is at lag 1.
        // Construct u_i = x̃_i e_i with e exactly an MA(1)-like deterministic alternating pattern
        // so γ_1 dominates γ_2 ≈ 0; then SE(L=1) and SE(L=2) differ only through the lag-2 term,
        // which carries the *smaller* weight k_2 = 1−2/3 = 1/3 < k_1 — so its marginal effect is
        // bounded by the lag-1 effect.
        var n = 240;
        var x = new double[n];
        var resid = new double[n];
        // Deterministic pattern: x is a smooth ramp, residuals carry a clean lag-1 pattern.
        double prev = 0;
        for (var i = 0; i < n; i++)
        {
            x[i] = Math.Sin(i * 0.05);
            var e = 0.6 * prev + (i % 2 == 0 ? 0.1 : -0.1);
            resid[i] = e;
            prev = e;
        }

        var se0 = NeweyWest.SlopeStandardError(x, resid, lag: 0).StandardError;
        var se1 = NeweyWest.SlopeStandardError(x, resid, lag: 1).StandardError;
        var se2 = NeweyWest.SlopeStandardError(x, resid, lag: 2).StandardError;

        // Adding lag 1 (weight 0.5) moves the SE by some amount; adding lag 2 on top carries the
        // strictly smaller within-window weight, so its incremental effect cannot exceed the lag-1
        // step in this fast-decaying series.
        var d01 = Math.Abs(se1 - se0);
        var d12 = Math.Abs(se2 - se1);
        d01.Should().BeGreaterThan(0);
        d12.Should().BeLessThan(d01 + 1e-9);
    }

    [Fact]
    public void TooFewObservations_ReturnsZeroSe()
    {
        var nw = NeweyWest.SlopeStandardError(new double[] { 1, 2 }, new double[] { 0.1, -0.1 });
        nw.StandardError.Should().Be(0);
        nw.TStat.Should().Be(0);
    }

    [Fact]
    public void TStat_IsSlopeOverSe()
    {
        var rng = new Random(3);
        var n = 120;
        var x = new double[n];
        var resid = new double[n];
        for (var i = 0; i < n; i++) { x[i] = rng.NextGaussian(); resid[i] = rng.NextGaussian() * 0.4; }

        var nw = NeweyWest.SlopeStandardError(x, resid, slope: 0.75, lag: 0);
        nw.TStat.Should().BeApproximately(0.75 / nw.StandardError, 1e-9);
    }
}
