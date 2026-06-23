using FluentAssertions;
using TradingTerminal.Core.Quant;
using Xunit;

namespace TradingTerminal.Tests.Quant;

public sealed class CurveFittingTests
{
    private static double[] Range(int n)
    {
        var x = new double[n];
        for (var i = 0; i < n; i++) x[i] = i;
        return x;
    }

    private static double[] Map(double[] x, Func<double, double> f)
    {
        var y = new double[x.Length];
        for (var i = 0; i < x.Length; i++) y[i] = f(x[i]);
        return y;
    }

    [Fact]
    public void Linear_RecoversExactLine()
    {
        var x = Range(10);
        var y = Map(x, v => 2.5 * v - 4.0);

        var fit = CurveFitting.FitEvaluate(CurveFitKind.Linear, x, y, x);

        fit.Should().NotBeNull();
        for (var i = 0; i < x.Length; i++) fit![i].Should().BeApproximately(y[i], 1e-9);
    }

    [Fact]
    public void Quadratic_RecoversExactParabola()
    {
        var x = Range(12);
        var y = Map(x, v => 0.7 * v * v - 3.0 * v + 11.0);

        var fit = CurveFitting.FitEvaluate(CurveFitKind.Quadratic, x, y, x);

        fit.Should().NotBeNull();
        for (var i = 0; i < x.Length; i++) fit![i].Should().BeApproximately(y[i], 1e-8);
    }

    [Fact]
    public void Cubic_RecoversExactCubic()
    {
        var x = Range(14);
        var y = Map(x, v => 0.05 * v * v * v - 0.6 * v * v + 2.0 * v + 7.0);

        var fit = CurveFitting.FitEvaluate(CurveFitKind.Cubic, x, y, x);

        fit.Should().NotBeNull();
        for (var i = 0; i < x.Length; i++) fit![i].Should().BeApproximately(y[i], 1e-7);
    }

    [Fact]
    public void TheilSen_IgnoresSingleOutlier()
    {
        var x = Range(10);
        var y = Map(x, v => 2.0 * v + 1.0);
        y[4] += 100.0; // one wild POC bar must not bend the robust line

        var fit = CurveFitting.FitEvaluate(CurveFitKind.TheilSen, x, y, x);

        fit.Should().NotBeNull();
        for (var i = 0; i < x.Length; i++) fit![i].Should().BeApproximately(2.0 * i + 1.0, 1e-9);
    }

    [Fact]
    public void Exponential_RecoversExponential()
    {
        var x = Range(10);
        var y = Map(x, v => 2.0 * Math.Exp(0.3 * v));

        var fit = CurveFitting.FitEvaluate(CurveFitKind.Exponential, x, y, x);

        fit.Should().NotBeNull();
        for (var i = 0; i < x.Length; i++) fit![i].Should().BeApproximately(y[i], 1e-6 * y[i]);
    }

    [Fact]
    public void Exponential_ReturnsNull_WhenYNotPositive()
    {
        var x = Range(5);
        var y = new[] { 1.0, 2.0, 0.0, 4.0, 5.0 };

        CurveFitting.FitEvaluate(CurveFitKind.Exponential, x, y, x).Should().BeNull();
    }

    [Fact]
    public void Logarithmic_RecoversLogCurve()
    {
        var x = Range(15);
        var y = Map(x, v => 2.0 + 3.0 * Math.Log(v + 1.0)); // shift resolves to +1 for 0-based x

        var fit = CurveFitting.FitEvaluate(CurveFitKind.Logarithmic, x, y, x);

        fit.Should().NotBeNull();
        for (var i = 0; i < x.Length; i++) fit![i].Should().BeApproximately(y[i], 1e-8);
    }

    [Fact]
    public void Lowess_ReproducesLinearDataExactly()
    {
        // A local *linear* smoother is exact on globally linear data, whatever the weights.
        var x = Range(20);
        var y = Map(x, v => 3.0 * v + 2.0);

        var fit = CurveFitting.FitEvaluate(CurveFitKind.Lowess, x, y, x);

        fit.Should().NotBeNull();
        for (var i = 0; i < x.Length; i++) fit![i].Should().BeApproximately(y[i], 1e-7);
    }

    [Fact]
    public void Lowess_TracksSmoothCurveClosely()
    {
        var x = Range(30);
        var y = Map(x, v => Math.Sin(v / 5.0));

        var fit = CurveFitting.FitEvaluate(CurveFitKind.Lowess, x, y, x);

        fit.Should().NotBeNull();
        for (var i = 0; i < x.Length; i++) fit![i].Should().BeApproximately(y[i], 0.25);
    }

    [Fact]
    public void Evaluates_AtAbscissaeOutsideTheFitSamples()
    {
        var x = new double[] { 2, 3, 5, 8 };
        var y = Map(x, v => 1.5 * v + 0.5);
        var evalX = new double[] { 0, 1, 9, 10 }; // columns whose POC was NaN

        var fit = CurveFitting.FitEvaluate(CurveFitKind.Linear, x, y, evalX);

        fit.Should().NotBeNull();
        for (var i = 0; i < evalX.Length; i++) fit![i].Should().BeApproximately(1.5 * evalX[i] + 0.5, 1e-9);
    }

    [Theory]
    [InlineData(CurveFitKind.Linear, 1)]
    [InlineData(CurveFitKind.Quadratic, 2)]
    [InlineData(CurveFitKind.Cubic, 3)]
    [InlineData(CurveFitKind.TheilSen, 1)]
    [InlineData(CurveFitKind.Exponential, 1)]
    [InlineData(CurveFitKind.Logarithmic, 1)]
    [InlineData(CurveFitKind.Lowess, 2)]
    public void ReturnsNull_BelowMinimumPoints(CurveFitKind kind, int count)
    {
        var x = Range(count);
        var y = Map(x, v => v + 1.0);

        CurveFitting.FitEvaluate(kind, x, y, x).Should().BeNull();
    }

    [Theory]
    [InlineData(CurveFitKind.Linear)]
    [InlineData(CurveFitKind.Quadratic)]
    [InlineData(CurveFitKind.Cubic)]
    [InlineData(CurveFitKind.TheilSen)]
    public void ReturnsNull_WhenAllXCoincide(CurveFitKind kind)
    {
        var x = new[] { 3.0, 3.0, 3.0, 3.0, 3.0 };
        var y = new[] { 1.0, 2.0, 3.0, 4.0, 5.0 };

        CurveFitting.FitEvaluate(kind, x, y, x).Should().BeNull();
    }

    [Fact]
    public void ReturnsNull_OnMismatchedLengths()
    {
        CurveFitting.FitEvaluate(CurveFitKind.Linear, Range(5), Range(4), Range(5)).Should().BeNull();
    }
}
