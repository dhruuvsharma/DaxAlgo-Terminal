using DaxAlgo.Sdk.Quant;
using Xunit;

namespace DaxAlgo.Sandbox.Samples.Tests;

/// <summary>
/// The online learners, which had no tests at all until 2026-09-01 — they were duplicated
/// byte-for-byte across two tool projects, under a namespace claiming an assembly neither of them
/// was, and reachable by nothing an author could write.
///
/// <para>Each test here is a property that fails loudly if the maths is wrong, rather than a
/// characterisation of what the code currently returns. A learner that has drifted still trains; it
/// simply trains toward the wrong thing, which no smoke test would notice.</para>
/// </summary>
public sealed class OnlineLearnerTests
{
    /// <summary>Feature vector for the exact linear target below: bias, x, x².</summary>
    private static double[] Features(double x) => [1d, x, x * x];

    private static double Target(double x) => 3d + (2d * x) - (0.5d * x * x);

    [Fact]
    public void Recursive_least_squares_converges_on_an_exact_linear_target()
    {
        // Consistency, asserted as CONVERGENCE rather than against a tolerance somebody picked.
        //
        // `initialDiagonal` is a finite prior covariance, so RLS starts as a very lightly ridged fit
        // and the penalty decays as data accumulates. On a noiseless system that leaves a real
        // residual — about 1e-5 after 600 samples with the default prior — and it is the algorithm
        // behaving correctly, not drift. Asserting a fixed tolerance here would be tuning the test to
        // the fixture, and would still pass a learner that had stopped improving.
        static double ErrorAfter(int samples, double prior)
        {
            var rls = new OnlineLinearRegression(3, lambda: 1d, initialDiagonal: prior);
            for (var i = 0; i < samples; i++)
            {
                var x = -3d + (6d * i / (samples - 1));   // same interval however dense
                rls.Update(Features(x), Target(x));
            }

            return Math.Abs(rls.Predict(Features(1.7d)) - Target(1.7d));
        }

        var coarse = ErrorAfter(60, 1e3d);
        var fine = ErrorAfter(600, 1e3d);
        Assert.True(fine < coarse, $"error must fall with data: {coarse} then {fine}");

        // A diffuse prior removes the ridge, and then the fit is exact. This is what the argument is
        // FOR, and an author who does not know it will wonder why a noiseless fit is slightly off.
        Assert.True(ErrorAfter(600, 1e9d) < 1e-9d, "a diffuse prior recovers the target exactly");
    }

    [Fact]
    public void Forgetting_tracks_a_target_that_changes()
    {
        // The reason to use RLS with λ < 1 at all. Trained on one regime and then another, a
        // forgetting learner must end up near the SECOND; λ = 1 is classical OLS and would average
        // the two, which on a live book is the difference between a signal and a lagging mean.
        var forgetting = new OnlineLinearRegression(1, lambda: 0.9d);
        var remembering = new OnlineLinearRegression(1, lambda: 1d);

        foreach (var learner in new[] { forgetting, remembering })
        {
            for (var i = 0; i < 100; i++) learner.Update([1d], 10d);
            for (var i = 0; i < 100; i++) learner.Update([1d], 20d);
        }

        Assert.True(
            Math.Abs(forgetting.Predict([1d]) - 20d) < Math.Abs(remembering.Predict([1d]) - 20d),
            "a forgetting learner must end nearer the new regime than one that remembers everything");
    }

    [Fact]
    public void Logistic_regression_stays_a_probability_and_separates_a_separable_set()
    {
        var logistic = new OnlineLogisticRegression(2, learningRate: 0.5d, l2: 0d);

        for (var pass = 0; pass < 400; pass++)
        {
            logistic.Update([1d, 1d], 1d);
            logistic.Update([1d, -1d], 0d);
        }

        var positive = logistic.Predict([1d, 1d]);
        var negative = logistic.Predict([1d, -1d]);

        Assert.InRange(positive, 0d, 1d);
        Assert.InRange(negative, 0d, 1d);
        Assert.True(positive > 0.8d, $"expected a confident positive, got {positive}");
        Assert.True(negative < 0.2d, $"expected a confident negative, got {negative}");
    }

    [Fact]
    public void An_extreme_score_does_not_overflow_the_sigmoid()
    {
        // exp(-z) overflows long before a feature vector does anything unusual, and the failure is a
        // NaN that propagates into every later update rather than an exception anyone would see.
        var logistic = new OnlineLogisticRegression(1, learningRate: 5d, l2: 0d);
        for (var i = 0; i < 500; i++) logistic.Update([1000d], 1d);

        var p = logistic.Predict([1_000_000d]);

        Assert.True(double.IsFinite(p));
        Assert.InRange(p, 0d, 1d);
    }

    [Fact]
    public void Gradient_descent_converges_on_the_same_target_as_RLS()
    {
        // A cheaper, higher-variance route to the same answer. If the two disagree on a noiseless
        // linear system, one of them is wrong.
        var sgd = new OnlineGradientDescent(3, learningRate: 0.02d, l2: 0d);

        for (var pass = 0; pass < 60; pass++)
        {
            for (var i = 0; i < 100; i++)
            {
                var x = -1.5d + (i * 0.03d);
                sgd.Update(Features(x), Target(x));
            }
        }

        Assert.Equal(Target(0.8d), sgd.Predict(Features(0.8d)), 1);
    }

    [Fact]
    public void The_scaler_standardises_and_leaves_the_bias_alone()
    {
        // Standardising a constant zeroes it, which silently removes the intercept from every learner
        // downstream — hence the pass-through dimensions.
        var scaler = new OnlineFeatureScaler(2, passthroughDimensions: 1);

        for (var i = 0; i < 500; i++) scaler.Observe([1d, 100d + (i % 7)]);

        // Writes into a caller-owned buffer: this runs per tick, and allocating an array per call is
        // exactly the per-frame allocation the drawing rules forbid one layer up.
        var scaled = new double[2];
        scaler.Transform([1d, 103d], scaled);

        Assert.Equal(1d, scaled[0]);
        Assert.True(Math.Abs(scaled[1]) < 3d, "a value inside the observed range must scale to a small z");
    }

    [Fact]
    public void State_round_trips_through_save_and_restore()
    {
        // The warm start. A learner restored from its own snapshot must predict identically, or a
        // restart quietly discards everything it took a session to learn.
        var trained = new OnlineLinearRegression(3, lambda: 0.99d);
        for (var i = 0; i < 150; i++)
        {
            var x = -2d + (i * 0.02d);
            trained.Update(Features(x), Target(x));
        }

        var restored = new OnlineLinearRegression(3, lambda: 0.99d);
        restored.LoadState(trained.SaveState());

        Assert.Equal(trained.Predict(Features(0.4d)), restored.Predict(Features(0.4d)), 9);
        Assert.Equal(trained.Samples, restored.Samples);
    }

    [Fact]
    public void A_restore_refuses_state_from_a_different_learner()
    {
        var logistic = new OnlineLogisticRegression(3);
        var rls = new OnlineLinearRegression(3);

        Assert.Throws<ArgumentException>(() => rls.LoadState(logistic.SaveState()));
    }

    [Fact]
    public void The_brier_score_rewards_calibration_and_names_the_baseline_to_beat()
    {
        // A perfect forecaster scores 0; always saying 50% scores 0.25. The base rate is carried
        // alongside because a Brier score alone cannot be read — beating 0.25 is meaningless on an
        // event that happens 5% of the time.
        var sure = new RollingBrierScore(100);
        var unsure = new RollingBrierScore(100);

        for (var i = 0; i < 100; i++)
        {
            var occurred = i % 2 == 0;
            sure.Score(occurred ? 1d : 0d, occurred);
            unsure.Score(0.5d, occurred);
        }

        Assert.Equal(0d, sure.Snapshot().Brier, 9);
        Assert.Equal(0.25d, unsure.Snapshot().Brier, 9);
        Assert.Equal(0.5d, unsure.Snapshot().BaseRate, 9);
    }

    [Fact]
    public void Forecast_metrics_report_error_and_direction_separately()
    {
        // Two different failures. A forecaster can be small-error and directionless (it predicts
        // nothing much, and is right about the magnitude) or large-error and directionally useful.
        var metrics = new RollingForecastMetrics(50);

        for (var i = 0; i < 50; i++)
        {
            var realised = i % 2 == 0 ? 2d : -2d;
            metrics.Score(realised / 2d, realised);   // right sign, half the size
        }

        var snapshot = metrics.Snapshot();

        Assert.Equal(1d, snapshot.MeanAbsoluteError, 9);
        Assert.Equal(1d, snapshot.DirectionalHitRate, 9);
    }
}
