using FluentAssertions;
using TradingTerminal.Core.Ml;
using Xunit;

namespace TradingTerminal.Tests.Ml;

/// <summary>
/// Coverage for the selectable online-learner family behind <see cref="IOnlineForecaster"/> — the
/// alternatives to RLS a window's "Model" selector switches between: each learns its target, each
/// round-trips its state through the seam, the factory/tag mapping is consistent, and a predictor
/// built with a non-default learner tags its artifact accordingly (so the registry versions
/// algorithms independently and a restore refuses a mismatched algorithm).
/// </summary>
public sealed class ForecasterAlgorithmTests
{
    private const double Tick = 0.25;
    private const string Instrument = "SIM:BTCUSD";

    [Fact]
    public void OnlineGradientDescent_LearnsALinearRelationship()
    {
        var rng = new Random(21);
        var ogd = new OnlineGradientDescent(dimensions: 2);
        for (var i = 0; i < 12_000; i++)
        {
            var x1 = rng.NextDouble() * 2 - 1;
            var x2 = rng.NextDouble() * 2 - 1;
            var y = 2.0 * x1 - 0.5 * x2 + (rng.NextDouble() - 0.5) * 0.05;
            ogd.Update(new[] { x1, x2 }, y);
        }

        // Probe the learned linear map through the public interface: ŷ(1,0)=w0, ŷ(0,1)=w1.
        ogd.Predict(new[] { 1.0, 0.0 }).Should().BeApproximately(2.0, 0.2);
        ogd.Predict(new[] { 0.0, 1.0 }).Should().BeApproximately(-0.5, 0.2);
        ogd.Samples.Should().Be(12_000);
    }

    [Fact]
    public void Logistic_SeparatesALinearlySeparableClass_AndStaysInUnitInterval()
    {
        var rng = new Random(7);
        var model = new OnlineLogisticRegression(dimensions: 2);
        for (var i = 0; i < 6_000; i++)
        {
            var x1 = rng.NextDouble() * 2 - 1;
            var x2 = rng.NextDouble() * 2 - 1;
            var label = 3.0 * x1 - 2.0 * x2 > 0 ? 1.0 : 0.0;
            model.Update(new[] { x1, x2 }, label);
        }

        model.Predict(new[] { 1.0, -1.0 }).Should().BeGreaterThan(0.5);   // 3 + 2 = 5 > 0
        model.Predict(new[] { -1.0, 1.0 }).Should().BeLessThan(0.5);      // -5 < 0
        model.Predict(new[] { 1.0, -1.0 }).Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public void EwmaBaseline_TracksTheTargetMean_IgnoringFeatures()
    {
        var ewma = new EwmaForecaster(dimensions: 3);
        var features = new[] { 1.0, 2.0, 3.0 };
        for (var i = 0; i < 300; i++) ewma.Update(features, 7.0);
        ewma.Predict(features).Should().BeApproximately(7.0, 1e-6);

        for (var i = 0; i < 400; i++) ewma.Update(features, 3.0);
        ewma.Predict(features).Should().BeApproximately(3.0, 0.05, "the EWMA decays onto the new level");
        // Prediction is independent of the feature vector.
        ewma.Predict(new[] { -9.0, 9.0, 0.0 }).Should().Be(ewma.Predict(features));
    }

    [Theory]
    [InlineData(LearnerKind.OnlineGradientDescent)]
    [InlineData(LearnerKind.EwmaBaseline)]
    [InlineData(LearnerKind.Logistic)]
    public void EveryLearner_SaveLoadState_ReproducesPredictions(LearnerKind kind)
    {
        var rng = new Random(3);
        var a = Forecasters.Create(kind, dimensions: 3, lambda: 0.99);
        for (var i = 0; i < 400; i++)
        {
            var x = new[] { 1.0, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1 };
            a.Update(x, rng.NextDouble());
        }

        var restored = Forecasters.Create(kind, dimensions: 3, lambda: 0.99);
        restored.LoadState(a.SaveState());

        restored.Samples.Should().Be(a.Samples);
        for (var i = 0; i < 20; i++)
        {
            var probe = new[] { 1.0, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1 };
            restored.Predict(probe).Should().Be(a.Predict(probe));
        }
    }

    [Fact]
    public void Forecasters_Factory_TagAndParse_AreConsistent()
    {
        foreach (LearnerKind kind in Enum.GetValues<LearnerKind>())
        {
            var learner = Forecasters.Create(kind, dimensions: 4, lambda: 0.99);
            learner.Dimensions.Should().Be(4);
            learner.Kind.Should().Be(Forecasters.Tag(kind));
            Forecasters.Parse(Forecasters.Tag(kind)).Should().Be(kind);
            Forecasters.DisplayName(kind).Should().NotBeNullOrWhiteSpace();
        }

        var act = () => Forecasters.Parse("nope");
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Predictor_WithNonDefaultLearner_TagsArtifact_AndRestoreEnforcesAlgorithm()
    {
        var ogd = new FootprintNextBarPredictor(Tick, new FootprintPredictorOptions(Learner: LearnerKind.OnlineGradientDescent));
        for (var i = 0; i < 150; i++) ogd.OnBarSealed(Bar(i, 100.0 + i * Tick), double.NaN);

        var artifact = ogd.CreateArtifact(Instrument, "1m");
        artifact.Algorithm.Should().Be("ogd");

        // Same algorithm restores; a different algorithm (default RLS) refuses the artifact.
        new FootprintNextBarPredictor(Tick, new FootprintPredictorOptions(Learner: LearnerKind.OnlineGradientDescent))
            .TryRestore(artifact).Should().BeTrue();
        new FootprintNextBarPredictor(Tick).TryRestore(artifact).Should().BeFalse();
    }

    private static FootprintBarSummary Bar(int index, double poc) =>
        new(
            StartUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index),
            Poc: poc, BuyPoc: poc + Tick, SellPoc: poc - Tick,
            High: poc + 2 * Tick, Low: poc - 2 * Tick,
            ValueAreaHigh: poc + Tick, ValueAreaLow: poc - Tick,
            TotalVolume: 1_000, Delta: 200, CumulativeDelta: 200L * (index + 1),
            StackedBuy: 1, StackedSell: 0, QualityMultiplier: 1.0);
}
