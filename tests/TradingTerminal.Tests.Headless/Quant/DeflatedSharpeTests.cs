using FluentAssertions;
using TradingTerminal.Core.Quant;
using Xunit;

namespace TradingTerminal.Tests.Quant;

public sealed class DeflatedSharpeTests
{
    [Fact]
    public void Dsr_DecreasesAsTrialCountIncreases()
    {
        const double observed = 0.15;     // per-period Sharpe
        const double trialVar = 0.01;
        const double skew = 0.0, kurt = 3.0;
        const int t = 500;

        var few = DeflatedSharpe.Compute(observed, trials: 5, trialVar, skew, kurt, t);
        var many = DeflatedSharpe.Compute(observed, trials: 500, trialVar, skew, kurt, t);

        // More trials ⇒ higher expected max under the null ⇒ harder to clear ⇒ lower DSR.
        many.ExpectedMaxSharpe.Should().BeGreaterThan(few.ExpectedMaxSharpe);
        many.Dsr.Should().BeLessThan(few.Dsr);
    }

    [Fact]
    public void SingleTrial_ZeroTrialVariance_ReducesToPlainProbabilisticSharpe()
    {
        const double observed = 0.2;
        const int t = 250;

        var single = DeflatedSharpe.Compute(observed, trials: 1, trialSharpeVariance: 0.0,
            skew: 0.0, kurtosis: 3.0, sampleLength: t);

        // SR₀ = 0 ⇒ DSR = Φ( SR̂·sqrt(T-1) / sqrt(1 + SR̂²/2) ) — the plain PSR (skew0, kurt3).
        var denom = Math.Sqrt(1.0 + 0.5 * observed * observed);
        var z = observed * Math.Sqrt(t - 1.0) / denom;
        var expected = DeflatedSharpe.NormCdf(z);

        single.ExpectedMaxSharpe.Should().Be(0.0);
        single.Dsr.Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void NegativeSkew_LowersDsr()
    {
        const double observed = 0.2;
        const int t = 400;
        var positive = DeflatedSharpe.Compute(observed, 1, 0.0, skew: 0.5, kurtosis: 3.0, t);
        var negative = DeflatedSharpe.Compute(observed, 1, 0.0, skew: -0.5, kurtosis: 3.0, t);

        // For a positive observed Sharpe, the denominator term −skew·SR̂ makes the variance
        // estimate larger when skew is negative ⇒ smaller z ⇒ lower DSR.
        negative.Dsr.Should().BeLessThan(positive.Dsr);
    }

    [Fact]
    public void HigherKurtosis_LowersDsr()
    {
        const double observed = 0.2;
        const int t = 400;
        var thin = DeflatedSharpe.Compute(observed, 1, 0.0, skew: 0.0, kurtosis: 3.0, t);
        var fat = DeflatedSharpe.Compute(observed, 1, 0.0, skew: 0.0, kurtosis: 9.0, t);

        // (kurt-1)/4·SR̂² grows with kurtosis ⇒ larger denominator ⇒ smaller z ⇒ lower DSR.
        fat.Dsr.Should().BeLessThan(thin.Dsr);
    }

    [Fact]
    public void NormCdf_NormInv_RoundTrip_OverAGrid()
    {
        for (var x = -3.0; x <= 3.0; x += 0.25)
        {
            var p = DeflatedSharpe.NormCdf(x);
            var back = DeflatedSharpe.NormInv(p);
            back.Should().BeApproximately(x, 1e-4);
        }
    }

    [Fact]
    public void NormCdf_KnownValues()
    {
        DeflatedSharpe.NormCdf(0.0).Should().BeApproximately(0.5, 1e-6);
        DeflatedSharpe.NormCdf(1.0).Should().BeApproximately(0.8413447, 1e-5);
        DeflatedSharpe.NormCdf(-1.0).Should().BeApproximately(0.1586553, 1e-5);
    }

    [Fact]
    public void NormInv_KnownValues()
    {
        DeflatedSharpe.NormInv(0.5).Should().BeApproximately(0.0, 1e-6);
        DeflatedSharpe.NormInv(0.975).Should().BeApproximately(1.959964, 1e-4);
        DeflatedSharpe.NormInv(0.025).Should().BeApproximately(-1.959964, 1e-4);
    }

    [Fact]
    public void SampleLengthBelowTwo_YieldsNaNDsr()
    {
        var res = DeflatedSharpe.Compute(0.2, 10, 0.01, 0.0, 3.0, sampleLength: 1);
        double.IsNaN(res.Dsr).Should().BeTrue();
    }
}
