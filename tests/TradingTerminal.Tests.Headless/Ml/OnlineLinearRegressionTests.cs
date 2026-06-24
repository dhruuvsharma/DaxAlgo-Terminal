using FluentAssertions;
using TradingTerminal.Core.Ml;
using Xunit;

namespace TradingTerminal.Tests.Ml;

public sealed class OnlineLinearRegressionTests
{
    [Fact]
    public void ConvergesToTrueBetaOnNoisyLinearData()
    {
        // y = 2·x1 - 0.5·x2 + noise. After ~1000 samples RLS with λ=1 should be close.
        var rng = new Random(42);
        var model = new OnlineLinearRegression(dimensions: 2, lambda: 1.0);
        for (var i = 0; i < 2000; i++)
        {
            var x1 = rng.NextDouble() * 2 - 1;
            var x2 = rng.NextDouble() * 2 - 1;
            var y = 2.0 * x1 - 0.5 * x2 + (rng.NextDouble() - 0.5) * 0.05;
            model.Update(new[] { x1, x2 }, y);
        }

        model.Coefficients[0].Should().BeApproximately(2.0, 0.05);
        model.Coefficients[1].Should().BeApproximately(-0.5, 0.05);
        model.Samples.Should().Be(2000);
    }

    [Fact]
    public void ForgettingFactorAdaptsToRegimeChange()
    {
        // First half: β = 5·x. Second half: β = -3·x. With λ = 0.95 the model should
        // adapt; with λ = 1 it averages and ends up between the two.
        var rng = new Random(7);
        var fast = new OnlineLinearRegression(1, lambda: 0.95);
        var slow = new OnlineLinearRegression(1, lambda: 1.0);

        for (var i = 0; i < 500; i++)
        {
            var x = rng.NextDouble() * 2 - 1;
            fast.Update(new[] { x }, 5 * x);
            slow.Update(new[] { x }, 5 * x);
        }
        for (var i = 0; i < 500; i++)
        {
            var x = rng.NextDouble() * 2 - 1;
            fast.Update(new[] { x }, -3 * x);
            slow.Update(new[] { x }, -3 * x);
        }

        fast.Coefficients[0].Should().BeLessThan(0, "λ=0.95 should adapt to the second regime");
        Math.Abs(slow.Coefficients[0]).Should().BeLessThan(2.5, "λ=1.0 averages both regimes");
    }

    [Fact]
    public void MismatchedFeatureLength_Throws()
    {
        var m = new OnlineLinearRegression(2);
        var act = () => m.Update(new[] { 1.0 }, 0.5);
        act.Should().Throw<ArgumentException>();
    }
}
