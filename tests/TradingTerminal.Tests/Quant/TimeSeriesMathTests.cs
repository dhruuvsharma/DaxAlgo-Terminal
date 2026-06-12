using FluentAssertions;
using TradingTerminal.Core.Quant.TimeSeries;
using Xunit;

namespace TradingTerminal.Tests.Quant;

/// <summary>
/// Offline tests for the Machine Learning menu's time-series math in Core — stationarity tests,
/// transforms, ARIMA, GARCH, and the Kalman filters. All synthetic data is seeded, so every
/// distribution-based assertion is reproducible.
/// </summary>
public sealed class TimeSeriesMathTests
{
    // ── Synthetic series ──────────────────────────────────────────────────────────────────────────

    private static double[] RandomWalk(int n, int seed, double drift = 0.0, double sigma = 1.0)
    {
        var rng = new Random(seed);
        var x = new double[n];
        x[0] = 100.0;
        for (var i = 1; i < n; i++) x[i] = x[i - 1] + drift + sigma * rng.NextGaussian();
        return x;
    }

    private static double[] WhiteNoise(int n, int seed, double sigma = 1.0)
    {
        var rng = new Random(seed);
        var x = new double[n];
        for (var i = 0; i < n; i++) x[i] = sigma * rng.NextGaussian();
        return x;
    }

    private static double[] Ar1(int n, int seed, double phi, double sigma = 1.0)
    {
        var rng = new Random(seed);
        var x = new double[n];
        for (var i = 1; i < n; i++) x[i] = phi * x[i - 1] + sigma * rng.NextGaussian();
        return x;
    }

    // ── Stationarity tests ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Adf_rejects_unit_root_for_white_noise()
    {
        var result = StationarityTests.Adf(WhiteNoise(500, seed: 7));
        result.Should().NotBeNull();
        result!.IsStationary.Should().BeTrue();
        result.Statistic.Should().BeLessThan(-2.862);
    }

    [Fact]
    public void Adf_does_not_reject_unit_root_for_random_walk()
    {
        var result = StationarityTests.Adf(RandomWalk(500, seed: 11));
        result.Should().NotBeNull();
        result!.IsStationary.Should().BeFalse();
    }

    [Fact]
    public void Kpss_passes_white_noise_and_flags_random_walk()
    {
        StationarityTests.Kpss(WhiteNoise(500, seed: 13))!.IsStationary.Should().BeTrue();
        StationarityTests.Kpss(RandomWalk(500, seed: 17))!.IsStationary.Should().BeFalse();
    }

    [Fact]
    public void Acf_of_ar1_decays_geometrically()
    {
        var acf = StationarityTests.Acf(Ar1(4000, seed: 19, phi: 0.8), maxLag: 5);
        acf[0].Should().BeApproximately(0.8, 0.06);
        acf[1].Should().BeApproximately(0.64, 0.08);
        acf[0].Should().BeGreaterThan(acf[2]);
    }

    // ── Transforms ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FirstDifference_of_random_walk_is_adf_stationary()
    {
        var walk = RandomWalk(400, seed: 23);
        var (diffed, consumed) = SeriesTransforms.Apply(walk, SeriesTransform.FirstDifference);
        consumed.Should().Be(1);
        diffed.Should().HaveCount(399);
        StationarityTests.Adf(diffed)!.IsStationary.Should().BeTrue();
    }

    [Fact]
    public void FracDiff_weights_shrink_and_alignment_is_reported()
    {
        var walk = RandomWalk(400, seed: 29);
        var (series, consumed) = SeriesTransforms.FracDiff(walk, d: 0.4);
        consumed.Should().BeGreaterThan(0);
        series.Length.Should().Be(400 - consumed);
        // d→1 reduces to (almost) plain differencing.
        var (almostDiff, _) = SeriesTransforms.FracDiff(walk, d: 1.0);
        var (plainDiff, _) = SeriesTransforms.Apply(walk, SeriesTransform.FirstDifference);
        almostDiff[^1].Should().BeApproximately(plainDiff[^1], 0.05);
    }

    [Fact]
    public void RollingMeanStd_matches_directly_computed_window()
    {
        var data = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var (mean, std) = SeriesTransforms.RollingMeanStd(data, window: 4);
        double.IsNaN(mean[2]).Should().BeTrue();
        mean[3].Should().BeApproximately(2.5, 1e-12);  // mean(1..4)
        mean[9].Should().BeApproximately(8.5, 1e-12);  // mean(7..10)
        std[9].Should().BeApproximately(Math.Sqrt(1.25), 1e-12);
    }

    // ── ARIMA ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Arima_recovers_ar1_coefficient()
    {
        var fit = ArimaModel.Fit(Ar1(3000, seed: 31, phi: 0.6), p: 1, d: 0, q: 0);
        fit.Should().NotBeNull();
        fit!.ArCoefficients[0].Should().BeApproximately(0.6, 0.06);
    }

    [Fact]
    public void Arima_d1_on_drifting_walk_forecasts_in_level_space()
    {
        var walk = RandomWalk(600, seed: 37, drift: 0.5, sigma: 0.8);
        var fit = ArimaModel.Fit(walk, p: 1, d: 1, q: 0);
        fit.Should().NotBeNull();

        var forecast = ArimaModel.Forecast(fit!, walk, horizon: 10);
        forecast.Should().HaveCount(10);
        // With drift +0.5/bar the 10-step point forecast must sit above the last level.
        forecast[9].Mean.Should().BeGreaterThan(walk[^1]);
        // Bands widen monotonically.
        var w1 = forecast[0].Upper95 - forecast[0].Lower95;
        var w10 = forecast[9].Upper95 - forecast[9].Lower95;
        w10.Should().BeGreaterThan(w1);
        // And contain the point forecast.
        forecast[9].Lower95.Should().BeLessThan(forecast[9].Mean);
        forecast[9].Upper95.Should().BeGreaterThan(forecast[9].Mean);
    }

    [Fact]
    public void PsiWeights_for_ar1_are_powers_of_phi()
    {
        var psi = ArimaModel.PsiWeights(new[] { 0.5 }, Array.Empty<double>(), 4);
        psi.Should().Equal(new[] { 1.0, 0.5, 0.25, 0.125 }, (a, b) => Math.Abs(a - b) < 1e-12);
    }

    // ── GARCH ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Garch_fit_on_simulated_garch_recovers_persistence_regime()
    {
        // Simulate GARCH(1,1): ω=0.00001, α=0.10, β=0.85 (persistence 0.95).
        var rng = new Random(41);
        const double omega = 1e-5, alpha = 0.10, beta = 0.85;
        var n = 4000;
        var r = new double[n];
        var v = omega / (1 - alpha - beta);
        for (var t = 0; t < n; t++)
        {
            if (t > 0) v = omega + alpha * r[t - 1] * r[t - 1] + beta * v;
            r[t] = Math.Sqrt(v) * rng.NextGaussian();
        }

        var fit = GarchModel.Fit(r);
        fit.Should().NotBeNull();
        fit!.Persistence.Should().BeInRange(0.85, 0.999);
        fit.Alpha.Should().BeGreaterThan(0.02);
        fit.LongRunVariance.Should().BeGreaterThan(0);
        fit.ConditionalVariance.Should().HaveCount(n);
        fit.ConditionalVariance.Should().OnlyContain(x => x > 0);
    }

    [Fact]
    public void Garch_forecast_decays_toward_long_run_variance()
    {
        var rng = new Random(43);
        var r = new double[800];
        for (var i = 0; i < r.Length; i++) r[i] = 0.01 * rng.NextGaussian() * (i % 100 < 10 ? 3 : 1);

        var fit = GarchModel.Fit(r);
        fit.Should().NotBeNull();
        var far = fit!.ForecastVariance(500);
        far.Should().BeApproximately(fit.LongRunVariance, fit.LongRunVariance * 0.2);
    }

    // ── Kalman ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LocalLevel_converges_to_a_constant_signal()
    {
        var rng = new Random(47);
        var y = new double[300];
        for (var i = 0; i < y.Length; i++) y[i] = 50.0 + 0.5 * rng.NextGaussian();

        var result = KalmanFilters.LocalLevel(y, qOverR: 1e-3);
        result.Should().NotBeNull();
        result!.Level[^1].Should().BeApproximately(50.0, 0.5);
        // The filtered track is smoother than the observations.
        Variance(result.Level[100..]).Should().BeLessThan(Variance(y[100..]));
    }

    [Fact]
    public void LocalLinearTrend_tracks_a_ramp_without_systematic_lag()
    {
        var rng = new Random(53);
        var y = new double[400];
        for (var i = 0; i < y.Length; i++) y[i] = 10.0 + 0.25 * i + 0.5 * rng.NextGaussian();

        var result = KalmanFilters.LocalLinearTrend(y, qOverR: 1e-2);
        result.Should().NotBeNull();
        result!.Slope[^1].Should().BeApproximately(0.25, 0.1);
        result.Level[^1].Should().BeApproximately(y[^1], 2.0);
    }

    [Fact]
    public void DynamicRegression_recovers_a_constant_hedge_beta()
    {
        var rng = new Random(59);
        var n = 500;
        var x = new double[n];
        var y = new double[n];
        x[0] = 100;
        for (var i = 1; i < n; i++) x[i] = x[i - 1] + rng.NextGaussian();
        for (var i = 0; i < n; i++) y[i] = 5.0 + 1.5 * x[i] + 0.5 * rng.NextGaussian();

        var result = KalmanFilters.DynamicRegression(y, x, qOverR: 1e-4);
        result.Should().NotBeNull();
        result!.Slope[^1].Should().BeApproximately(1.5, 0.15);
    }

    [Fact]
    public void DynamicRegression_follows_a_beta_break()
    {
        var rng = new Random(61);
        var n = 600;
        var x = new double[n];
        var y = new double[n];
        x[0] = 100;
        for (var i = 1; i < n; i++) x[i] = x[i - 1] + rng.NextGaussian();
        for (var i = 0; i < n; i++)
        {
            var beta = i < 300 ? 1.0 : 2.0;
            y[i] = beta * x[i] + 0.5 * rng.NextGaussian();
        }

        var result = KalmanFilters.DynamicRegression(y, x, qOverR: 1e-2);
        result.Should().NotBeNull();
        result!.Slope[^1].Should().BeApproximately(2.0, 0.3);
        result.Slope[250].Should().BeApproximately(1.0, 0.3);
    }

    // ── OLS / Nelder-Mead foundations ─────────────────────────────────────────────────────────────

    [Fact]
    public void Ols_recovers_known_coefficients()
    {
        var rng = new Random(67);
        var n = 200;
        var x = new double[n][];
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            var a = rng.NextGaussian();
            var b = rng.NextGaussian();
            x[i] = new[] { 1.0, a, b };
            y[i] = 2.0 + 3.0 * a - 1.5 * b + 0.1 * rng.NextGaussian();
        }

        var fit = Ols.Fit(x, y);
        fit.Should().NotBeNull();
        fit!.Beta[0].Should().BeApproximately(2.0, 0.05);
        fit.Beta[1].Should().BeApproximately(3.0, 0.05);
        fit.Beta[2].Should().BeApproximately(-1.5, 0.05);
        fit.TStat(1).Should().BeGreaterThan(10);
    }

    [Fact]
    public void NelderMead_minimizes_rosenbrock()
    {
        var result = NelderMead.Minimize(
            v => Math.Pow(1 - v[0], 2) + 100 * Math.Pow(v[1] - v[0] * v[0], 2),
            new[] { -1.0, 1.0 },
            step: 0.5,
            maxIterations: 5000,
            tolerance: 1e-12);

        result.Should().NotBeNull();
        result!.Value.X[0].Should().BeApproximately(1.0, 0.05);
        result.Value.X[1].Should().BeApproximately(1.0, 0.05);
    }

    private static double Variance(double[] x)
    {
        double m = 0;
        foreach (var v in x) m += v;
        m /= x.Length;
        double s = 0;
        foreach (var v in x) s += (v - m) * (v - m);
        return s / (x.Length - 1);
    }
}
