using FluentAssertions;
using TradingTerminal.Core.Quant;
using Xunit;

namespace TradingTerminal.Tests.Quant;

public sealed class IsotonicCalibrationTests
{
    [Fact]
    public void AlreadyMonotoneData_IsIdentity_BinMeans()
    {
        // Strictly increasing y at distinct x ⇒ no pooling ⇒ knots == inputs.
        var c = new double[] { 1, 2, 3, 4, 5 };
        var r = new double[] { -0.2, -0.1, 0.0, 0.1, 0.3 };

        var map = IsotonicCalibration.Fit(c, r);

        map.X.Should().Equal(c);
        map.Y.Should().Equal(r);
        map.Counts.Sum().Should().Be(5);
        map.TotalSamples.Should().Be(5);
    }

    [Fact]
    public void AntiMonotoneSegment_PoolsToTheAverage()
    {
        // y decreases across x ⇒ PAVA pools every block into one mean.
        var c = new double[] { 1, 2, 3, 4 };
        var r = new double[] { 4, 3, 2, 1 };

        var map = IsotonicCalibration.Fit(c, r);

        // Fully pooled ⇒ a single block whose value is the overall mean (2.5).
        map.Y.Should().OnlyContain(y => Math.Abs(y - 2.5) < 1e-9);
        map.Counts.Sum().Should().Be(4);
    }

    [Fact]
    public void Output_IsNonDecreasing_OverAGrid()
    {
        var rng = new Random(321);
        var n = 300;
        var c = new double[n];
        var r = new double[n];
        for (var i = 0; i < n; i++)
        {
            c[i] = rng.NextDouble() * 10 - 5;
            r[i] = 0.05 * c[i] + rng.NextGaussian() * 0.4; // noisy monotone
        }

        var map = IsotonicCalibration.Fit(c, r);

        // Knot Y must be non-decreasing.
        for (var i = 1; i < map.Y.Length; i++)
            map.Y[i].Should().BeGreaterThanOrEqualTo(map.Y[i - 1] - 1e-12);

        // Evaluate over a dense grid: non-decreasing everywhere.
        double prev = double.NegativeInfinity;
        for (var g = -6.0; g <= 6.0; g += 0.05)
        {
            var v = map.Evaluate(g);
            v.Should().BeGreaterThanOrEqualTo(prev - 1e-9);
            prev = v;
        }
    }

    [Fact]
    public void Evaluate_InterpolatesBetweenKnots_AndClampsAtEnds()
    {
        var c = new double[] { 0, 10 };
        var r = new double[] { 0, 100 };

        var map = IsotonicCalibration.Fit(c, r);

        map.Evaluate(5).Should().BeApproximately(50.0, 1e-9);   // midpoint
        map.Evaluate(2.5).Should().BeApproximately(25.0, 1e-9);
        map.Evaluate(-100).Should().BeApproximately(0.0, 1e-9);  // clamp low
        map.Evaluate(1000).Should().BeApproximately(100.0, 1e-9); // clamp high
    }

    [Fact]
    public void PerRegionCounts_SumToTotal()
    {
        var rng = new Random(9);
        var n = 250;
        var c = new double[n];
        var r = new double[n];
        for (var i = 0; i < n; i++) { c[i] = rng.NextGaussian(); r[i] = rng.NextGaussian(); }

        var map = IsotonicCalibration.Fit(c, r);

        map.Counts.Sum().Should().Be(n);
        map.TotalSamples.Should().Be(n);
    }

    [Fact]
    public void MinSamples_Honored_BootstrapDetectableViaTotalSamples()
    {
        var c = new double[] { 1, 2, 3 };
        var r = new double[] { 0.1, 0.2, 0.3 };
        const int minSamples = 100;

        var map = IsotonicCalibration.Fit(c, r, minSamples);

        // The fit still runs on whatever is given (3 points) ...
        map.TotalSamples.Should().Be(3);
        // ... and the caller can detect "bootstrap mode" by comparing to minSamples.
        (map.TotalSamples < minSamples).Should().BeTrue();
    }

    [Fact]
    public void BinnedMean_Fallback_IsSane_OnSameData()
    {
        var c = new double[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var r = new double[] { 0, 0, 1, 1, 2, 2, 3, 3, 4, 4 };

        var map = IsotonicCalibration.BinnedMean(c, r, bins: 5);

        map.Counts.Sum().Should().Be(10);
        // Each non-empty bin's mean lies within the data range.
        map.Y.Should().OnlyContain(y => y >= 0 && y <= 4);
        // Monotone trend preserved on this clean data (bin means increase).
        for (var i = 1; i < map.Y.Length; i++)
            map.Y[i].Should().BeGreaterThanOrEqualTo(map.Y[i - 1] - 1e-9);
    }

    [Fact]
    public void BinnedMean_AllEqualComposites_CollapsesToSinglePoint()
    {
        var c = new double[] { 3, 3, 3, 3 };
        var r = new double[] { 1, 2, 3, 4 };

        var map = IsotonicCalibration.BinnedMean(c, r);

        map.X.Should().HaveCount(1);
        map.Y[0].Should().BeApproximately(2.5, 1e-9);
        map.Counts[0].Should().Be(4);
    }

    [Fact]
    public void EmptyInput_ReturnsEmptyMap_EvaluatesToZero()
    {
        var map = IsotonicCalibration.Fit(Array.Empty<double>(), Array.Empty<double>());
        map.X.Should().BeEmpty();
        map.Evaluate(0.5).Should().Be(0);
    }
}
