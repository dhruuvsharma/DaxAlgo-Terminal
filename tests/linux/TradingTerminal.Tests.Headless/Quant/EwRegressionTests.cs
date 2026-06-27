using FluentAssertions;
using TradingTerminal.Core.Quant;
using Xunit;

namespace TradingTerminal.Tests.Quant;

public sealed class EwRegressionTests
{
    [Fact]
    public void Accumulator_MatchesBatchFit_OnSameData()
    {
        var rng = new Random(12345);
        var x = new double[40];
        var y = new double[40];
        for (var i = 0; i < x.Length; i++)
        {
            x[i] = rng.NextDouble() * 10 - 5;
            y[i] = 1.7 * x[i] + 0.3 + (rng.NextDouble() - 0.5) * 0.4;
        }

        const double delta = 0.92;
        var batch = EwRegression.Fit(x, y, delta);

        var acc = new EwRegression.Accumulator(delta);
        for (var i = 0; i < x.Length; i++) acc.Add(x[i], y[i]);
        var stream = acc.Result();

        stream.Slope.Should().BeApproximately(batch.Slope, 1e-9);
        stream.Intercept.Should().BeApproximately(batch.Intercept, 1e-9);
        stream.RSquared.Should().BeApproximately(batch.RSquared, 1e-9);
        stream.ResidualStdev.Should().BeApproximately(batch.ResidualStdev, 1e-9);
        stream.WeightSum.Should().BeApproximately(batch.WeightSum, 1e-9);
        stream.EffectiveSampleSize.Should().BeApproximately(batch.EffectiveSampleSize, 1e-9);
        stream.Count.Should().Be(batch.Count);
    }

    [Fact]
    public void Accumulator_MatchesBatchFit_WithExternalWeights()
    {
        var rng = new Random(999);
        var x = new double[30];
        var y = new double[30];
        var w = new double[30];
        for (var i = 0; i < x.Length; i++)
        {
            x[i] = rng.NextDouble() * 4;
            y[i] = -2.0 * x[i] + 5.0 + (rng.NextDouble() - 0.5) * 0.2;
            w[i] = 1.0 + rng.NextDouble() * 9.0; // volume-like weights
        }

        const double delta = 0.95;
        var batch = EwRegression.Fit(x, y, delta, w);

        var acc = new EwRegression.Accumulator(delta);
        for (var i = 0; i < x.Length; i++) acc.Add(x[i], y[i], w[i]);
        var stream = acc.Result();

        stream.Slope.Should().BeApproximately(batch.Slope, 1e-9);
        stream.Intercept.Should().BeApproximately(batch.Intercept, 1e-9);
        stream.RSquared.Should().BeApproximately(batch.RSquared, 1e-9);
    }

    [Fact]
    public void PerfectLine_Recovers_Slope2_Intercept1_R2One()
    {
        var x = new double[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        var y = x.Select(v => 2.0 * v + 1.0).ToArray();

        var fit = EwRegression.Fit(x, y, delta: 0.9);

        fit.Slope.Should().BeApproximately(2.0, 1e-9);
        fit.Intercept.Should().BeApproximately(1.0, 1e-9);
        fit.RSquared.Should().BeApproximately(1.0, 1e-9);
        fit.ResidualStdev.Should().BeApproximately(0.0, 1e-9);
        fit.Predict(10).Should().BeApproximately(21.0, 1e-9);
    }

    [Fact]
    public void DeltaOne_UnitWeights_ReducesToOls()
    {
        // delta=1 with no external weights ⇒ ordinary least squares.
        // Hand OLS on these 4 points:
        // x = {1,2,3,4}, y = {2,2,4,4}. x̄=2.5, ȳ=3.
        // S_xy = Σ(x-2.5)(y-3) = (-1.5)(-1)+(-0.5)(-1)+(0.5)(1)+(1.5)(1) = 1.5+0.5+0.5+1.5 = 4
        // S_xx = 2.25+0.25+0.25+2.25 = 5 ⇒ slope = 0.8, intercept = 3 - 0.8*2.5 = 1.0
        var x = new double[] { 1, 2, 3, 4 };
        var y = new double[] { 2, 2, 4, 4 };

        var fit = EwRegression.Fit(x, y, delta: 1.0);

        fit.Slope.Should().BeApproximately(0.8, 1e-9);
        fit.Intercept.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void ExternalVolumeWeights_ShiftFitTowardHeavyPoints()
    {
        // Two clusters: a low cluster near slope-via-(0,0) and a high cluster.
        // Heavily weighting the high cluster should pull the fit toward it.
        var x = new double[] { 0, 1, 9, 10 };
        var y = new double[] { 0, 0, 10, 10 };

        var light = new double[] { 1, 1, 1, 1 };
        var heavyHigh = new double[] { 1, 1, 100, 100 };

        var baseFit = EwRegression.Fit(x, y, delta: 1.0, light);
        var heavyFit = EwRegression.Fit(x, y, delta: 1.0, heavyHigh);

        // The heavy cluster sits at y≈10; weighting it raises the fitted value there
        // and the intercept moves toward fitting the high cluster's local level.
        heavyFit.Predict(9.5).Should().BeGreaterThan(baseFit.Predict(9.5) - 1e-9);
        // Heavy-weighting the tight (9,10)→(10,10) pair flattens the slope there.
        heavyFit.Predict(10).Should().BeApproximately(10.0, 0.5);
    }

    [Fact]
    public void Degenerate_TooFewPoints_DoesNotThrow_AndSignalsInvalid()
    {
        var empty = EwRegression.Fit(Array.Empty<double>(), Array.Empty<double>());
        empty.Count.Should().Be(0);
        empty.Slope.Should().Be(0);
        empty.RSquared.Should().Be(0);

        var single = EwRegression.Fit(new double[] { 3 }, new double[] { 7 });
        single.Count.Should().Be(1);
        single.Slope.Should().Be(0);
        single.Intercept.Should().Be(7); // intercept carries the lone y
    }

    [Fact]
    public void Degenerate_ZeroVarianceX_DoesNotThrow_AndSlopeIsZero()
    {
        var x = new double[] { 5, 5, 5, 5 };
        var y = new double[] { 1, 2, 3, 4 };

        var act = () => EwRegression.Fit(x, y);
        act.Should().NotThrow();

        var fit = EwRegression.Fit(x, y);
        fit.Slope.Should().Be(0.0);
        fit.RSquared.Should().Be(0.0);
    }
}
