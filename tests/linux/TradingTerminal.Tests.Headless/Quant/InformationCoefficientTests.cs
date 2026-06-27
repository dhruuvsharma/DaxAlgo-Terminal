using FluentAssertions;
using TradingTerminal.Core.Quant;
using Xunit;

namespace TradingTerminal.Tests.Quant;

public sealed class InformationCoefficientTests
{
    [Fact]
    public void ScoreEqualsForwardReturn_PearsonIsOne()
    {
        var scores = new double[] { -2, -1, 0, 1, 2, 3 };
        var rets = scores.ToArray();

        var ic = InformationCoefficient.Compute(scores, rets);

        ic.Pearson.Should().BeApproximately(1.0, 1e-9);
        ic.Spearman.Should().BeApproximately(1.0, 1e-9);
        ic.SampleSize.Should().Be(6);
    }

    [Fact]
    public void ScoreEqualsNegativeReturn_PearsonIsMinusOne()
    {
        var scores = new double[] { -2, -1, 0, 1, 2, 3 };
        var rets = scores.Select(s => -s).ToArray();

        var ic = InformationCoefficient.Compute(scores, rets);

        ic.Pearson.Should().BeApproximately(-1.0, 1e-9);
        ic.Spearman.Should().BeApproximately(-1.0, 1e-9);
    }

    [Fact]
    public void IndependentSeries_IcNearZero()
    {
        var rng = new Random(1234);
        var n = 5000;
        var scores = new double[n];
        var rets = new double[n];
        for (var i = 0; i < n; i++) { scores[i] = rng.NextGaussian(); rets[i] = rng.NextGaussian(); }

        var ic = InformationCoefficient.Compute(scores, rets);

        ic.Pearson.Should().BeApproximately(0.0, 0.05);
        ic.Spearman.Should().BeApproximately(0.0, 0.05);
    }

    [Fact]
    public void Spearman_InvariantUnderMonotoneTransform_OfScores()
    {
        var rng = new Random(55);
        var n = 200;
        var scores = new double[n];
        var rets = new double[n];
        for (var i = 0; i < n; i++)
        {
            scores[i] = rng.NextGaussian();
            rets[i] = 0.6 * scores[i] + rng.NextGaussian() * 0.5;
        }

        var baseIc = InformationCoefficient.Compute(scores, rets);

        // exp() is strictly increasing → preserves ranks → Spearman unchanged.
        var transformed = scores.Select(Math.Exp).ToArray();
        var transIc = InformationCoefficient.Compute(transformed, rets);

        transIc.Spearman.Should().BeApproximately(baseIc.Spearman, 1e-9);
        // Pearson, by contrast, generally changes under a non-linear transform.
    }

    [Fact]
    public void NanEntries_AreSkipped_WithoutThrowing()
    {
        var scores = new double[] { 1, double.NaN, 2, 3, double.NaN, 4 };
        var rets = new double[] { 1, 5, 2, 3, 9, 4 };

        IcResult ic = default!;
        var act = () => ic = InformationCoefficient.Compute(scores, rets);
        act.Should().NotThrow();

        // Only the 4 non-NaN pairs (all on the line y=x) survive ⇒ Pearson 1, n=4.
        ic.SampleSize.Should().Be(4);
        ic.Pearson.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void FewerThanTwoValidPairs_ReturnsZero()
    {
        var ic = InformationCoefficient.Compute(new[] { 1.0, double.NaN }, new[] { double.NaN, 2.0 });
        ic.Pearson.Should().Be(0);
        ic.Spearman.Should().Be(0);
        ic.SampleSize.Should().Be(0);
    }

    [Fact]
    public void VectorForm_ComputesPerColumnIndependently()
    {
        // Column 0 = forward return (IC=1), column 1 = negated (IC=-1).
        var rets = new double[] { -2, -1, 0, 1, 2 };
        var cols = new double[5, 2];
        for (var i = 0; i < 5; i++)
        {
            cols[i, 0] = rets[i];
            cols[i, 1] = -rets[i];
        }

        var results = InformationCoefficient.Compute(cols, rets);

        results.Should().HaveCount(2);
        results[0].Pearson.Should().BeApproximately(1.0, 1e-9);
        results[1].Pearson.Should().BeApproximately(-1.0, 1e-9);
    }
}
