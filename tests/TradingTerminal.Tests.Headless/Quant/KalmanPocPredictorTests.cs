using FluentAssertions;
using TradingTerminal.Core.Quant;
using Xunit;

namespace TradingTerminal.Tests.Quant;

/// <summary>
/// Covers the constant-velocity Kalman POC predictor: it should lock onto a linear price ramp
/// (recovering its slope and extrapolating it), grow its forecast variance with the horizon, and
/// ignore degenerate observations.
/// </summary>
public sealed class KalmanPocPredictorTests
{
    [Fact]
    public void TracksLinearRamp_AndForecastsForward()
    {
        var kf = new KalmanPocPredictor(processNoise: 1e-3, measurementNoise: 1e-4);
        const double p0 = 100.0;
        const double slope = 0.25;
        for (var t = 0; t < 200; t++) kf.Update(p0 + slope * t);

        kf.IsInitialized.Should().BeTrue();
        kf.Velocity.Should().BeApproximately(slope, 1e-2);

        var last = p0 + slope * 199;
        kf.Price.Should().BeApproximately(last, 0.5);

        // Forecast 10 bars ahead ≈ last + 10·slope.
        var (price, variance) = kf.Forecast(10);
        price.Should().BeApproximately(last + 10 * slope, 0.5);
        variance.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ForecastVariance_GrowsWithHorizon()
    {
        var kf = new KalmanPocPredictor();
        for (var t = 0; t < 100; t++) kf.Update(50.0 + 0.1 * t);

        var near = kf.Forecast(1).Variance;
        var far = kf.Forecast(20).Variance;
        far.Should().BeGreaterThan(near);
    }

    [Fact]
    public void Uninitialized_ReturnsInfiniteVariance()
    {
        var kf = new KalmanPocPredictor();
        kf.IsInitialized.Should().BeFalse();
        var (price, variance) = kf.Forecast(5);
        price.Should().Be(0);
        double.IsPositiveInfinity(variance).Should().BeTrue();
    }

    [Fact]
    public void IgnoresInvalidObservations()
    {
        var kf = new KalmanPocPredictor();
        kf.Update(100.0);
        var price = kf.Price;
        kf.Update(double.NaN);
        kf.Update(0);
        kf.Update(-5);
        kf.Price.Should().Be(price);   // unchanged by the degenerate inputs
    }
}
