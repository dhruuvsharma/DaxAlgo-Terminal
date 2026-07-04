using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Ml;
using Xunit;

namespace TradingTerminal.Tests.Ml;

/// <summary>
/// Serialization / checkpoint-round-trip coverage for the model-persistence foundation: the online
/// learner and feature scaler save/restore their learned state faithfully, the two chart predictors
/// checkpoint into a <see cref="ModelArtifact"/> and restore warm, the artifact round-trips through
/// System.Text.Json unchanged, and a restore refuses a mismatched artifact rather than loading stale
/// weights against the wrong feature vector.
/// </summary>
public sealed class ModelSerializationTests
{
    private const double Tick = 0.25;
    private const string Instrument = "SIM:BTCUSD";

    // ── Primitive learners ───────────────────────────────────────────────────────────────────

    [Fact]
    public void OnlineLinearRegression_SaveLoad_ReproducesPredictionsExactly()
    {
        var rng = new Random(5);
        var a = new OnlineLinearRegression(dimensions: 4, lambda: 0.97);
        for (var i = 0; i < 300; i++)
        {
            var x = RandomFeatures(rng, 4);
            a.Update(x, 1.5 * x[1] - 0.7 * x[2] + 0.3 * x[3]);
        }

        var restored = new OnlineLinearRegression(dimensions: 4, lambda: 0.97);
        restored.LoadState(a.SaveState());

        restored.Samples.Should().Be(a.Samples);
        for (var i = 0; i < 20; i++)
        {
            var probe = RandomFeatures(rng, 4);
            restored.Predict(probe).Should().Be(a.Predict(probe), "a restored learner is a bit-for-bit copy");
        }

        // Continued learning stays in lockstep from the shared state.
        for (var i = 0; i < 50; i++)
        {
            var x = RandomFeatures(rng, 4);
            var y = 1.5 * x[1] - 0.7 * x[2] + 0.3 * x[3];
            a.Update(x, y);
            restored.Update(x, y);
        }
        var final = RandomFeatures(rng, 4);
        restored.Predict(final).Should().BeApproximately(a.Predict(final), 1e-9);
    }

    [Fact]
    public void OnlineLinearRegression_LoadState_RejectsDimensionMismatch()
    {
        var state = new OnlineLinearRegression(3).SaveState();
        var act = () => new OnlineLinearRegression(4).LoadState(state);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void OnlineFeatureScaler_SaveLoad_ReproducesTransformExactly()
    {
        var rng = new Random(9);
        var scaler = new OnlineFeatureScaler(dimensions: 3);
        for (var i = 0; i < 200; i++) scaler.Observe(RandomFeatures(rng, 3));

        var restored = new OnlineFeatureScaler(dimensions: 3);
        restored.LoadState(scaler.SaveState());

        var raw = RandomFeatures(rng, 3);
        var a = new double[3];
        var b = new double[3];
        scaler.Transform(raw, a);
        restored.Transform(raw, b);
        b.Should().Equal(a);
    }

    // ── Footprint predictor checkpoint ───────────────────────────────────────────────────────

    [Fact]
    public void Footprint_CreateArtifact_Then_Restore_LoadsWeightsAndIsReady()
    {
        var trained = TrainedFootprint(150);
        var artifact = trained.CreateArtifact(Instrument, "1m");

        var restored = new FootprintNextBarPredictor(Tick);
        restored.TryRestore(artifact).Should().BeTrue();
        restored.IsReady.Should().BeTrue("restoring trained weights makes the model ready immediately");

        var reArtifact = restored.CreateArtifact(Instrument, "1m");
        AssertBanksEqual(artifact.Bank("nextbar")!.Learners, reArtifact.Bank("nextbar")!.Learners);
        reArtifact.Scaler.Mean.Should().Equal(artifact.Scaler.Mean);
        reArtifact.Scaler.Variance.Should().Equal(artifact.Scaler.Variance);
        reArtifact.Scaler.Samples.Should().Be(artifact.Scaler.Samples);
        reArtifact.SamplesTrained.Should().Be(artifact.SamplesTrained);
        reArtifact.Scalar("ewma_log_volume").Should().Be(artifact.Scalar("ewma_log_volume"));
    }

    [Fact]
    public void Footprint_Restore_RejectsWrongModelKind_And_ShapeMismatch()
    {
        var artifact = TrainedFootprint(120).CreateArtifact(Instrument, "1m");

        // Wrong family: a footprint artifact must not load into an order-book predictor.
        new OrderBookMicroPredictor().TryRestore(artifact).Should().BeFalse();

        // Shape mismatch: an artifact trained with MaxHorizon 4 has a 4×5 bank; the default
        // predictor expects 8×5 and must refuse it.
        var small = TrainedFootprint(80, new FootprintPredictorOptions(MaxHorizon: 4))
            .CreateArtifact(Instrument, "1m");
        new FootprintNextBarPredictor(Tick).TryRestore(small).Should().BeFalse();

        // Feature-contract mismatch: a tampered dimension is refused before any weights load.
        var tampered = artifact with { Features = artifact.Features with { Dimension = 15 } };
        new FootprintNextBarPredictor(Tick).TryRestore(tampered).Should().BeFalse();
    }

    // ── Order-book predictor checkpoint ──────────────────────────────────────────────────────

    [Fact]
    public void OrderBook_CreateArtifact_Then_Restore_LoadsBothBanksAndIsReady()
    {
        var trained = TrainedOrderBook(300);
        var artifact = trained.CreateArtifact(Instrument, "250ms");

        var restored = new OrderBookMicroPredictor();
        restored.TryRestore(artifact).Should().BeTrue();
        restored.IsReady.Should().BeTrue();

        var reArtifact = restored.CreateArtifact(Instrument, "250ms");
        AssertBanksEqual(artifact.Bank("direction")!.Learners, reArtifact.Bank("direction")!.Learners);
        AssertBanksEqual(artifact.Bank("event")!.Learners, reArtifact.Bank("event")!.Learners);
        reArtifact.Scaler.Mean.Should().Equal(artifact.Scaler.Mean);
        reArtifact.Scalar("observed_tick").Should().Be(artifact.Scalar("observed_tick"));
        reArtifact.Scalar("ewma_log_depth").Should().Be(artifact.Scalar("ewma_log_depth"));
    }

    // ── On-disk format ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ModelArtifact_RoundTripsThroughJsonUnchanged()
    {
        var artifact = TrainedFootprint(120).CreateArtifact(Instrument, "1m");

        var json = JsonSerializer.Serialize(artifact, ModelArtifactJson.Options);
        var back = JsonSerializer.Deserialize<ModelArtifact>(json, ModelArtifactJson.Options);

        back.Should().NotBeNull();
        back!.Should().BeEquivalentTo(artifact, o => o.WithStrictOrdering());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static double[] RandomFeatures(Random rng, int d)
    {
        var x = new double[d];
        x[0] = 1.0; // bias
        for (var i = 1; i < d; i++) x[i] = rng.NextDouble() * 2 - 1;
        return x;
    }

    private static FootprintNextBarPredictor TrainedFootprint(int bars, FootprintPredictorOptions? options = null)
    {
        var ml = new FootprintNextBarPredictor(Tick, options);
        for (var i = 0; i < bars; i++) ml.OnBarSealed(FootprintBar(i, 100.0 + i * Tick), double.NaN);
        return ml;
    }

    private static OrderBookMicroPredictor TrainedOrderBook(int steps)
    {
        var ml = new OrderBookMicroPredictor();
        for (var i = 0; i < steps; i++) ml.OnStep(OrderBookStep(i, 100.0 + i * Tick));
        return ml;
    }

    private static void AssertBanksEqual(IReadOnlyList<ForecasterState> expected, IReadOnlyList<ForecasterState> actual)
    {
        actual.Should().HaveCount(expected.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            actual[i].Kind.Should().Be(expected[i].Kind);
            actual[i].Dimensions.Should().Be(expected[i].Dimensions);
            actual[i].Samples.Should().Be(expected[i].Samples);
            actual[i].Coefficients.Should().Equal(expected[i].Coefficients);
            actual[i].Covariance.Should().Equal(expected[i].Covariance);
        }
    }

    private static FootprintBarSummary FootprintBar(int index, double poc, long volume = 1_000, long delta = 200) =>
        new(
            StartUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index),
            Poc: poc, BuyPoc: poc + Tick, SellPoc: poc - Tick,
            High: poc + 2 * Tick, Low: poc - 2 * Tick,
            ValueAreaHigh: poc + Tick, ValueAreaLow: poc - Tick,
            TotalVolume: volume, Delta: delta, CumulativeDelta: delta * (index + 1),
            StackedBuy: 1, StackedSell: 0, QualityMultiplier: 1.0);

    private static OrderBookStepSummary OrderBookStep(int index, double microprice)
    {
        var halfSpread = Tick / 2.0;
        return new OrderBookStepSummary(
            TimestampUtc: new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).AddMilliseconds(250.0 * index),
            BestBid: microprice - halfSpread, BestAsk: microprice + halfSpread,
            BestBidSize: 180, BestAskSize: 20,
            Microprice: microprice, WeightedMid: microprice,
            ImbalanceL1: 0.8, ImbalanceCum: 0.8,
            BidDepthTop3: 300, AskDepthTop3: 300, BidDepthTopN: 1_000, AskDepthTopN: 1_000,
            LargestGapBid: Tick, LargestGapAsk: Tick, MinLevelGap: Tick,
            SweepCostBuy: 0.25, SweepCostSell: 0.25,
            SignedTradeFlow: 50, TradeCount: 5, TradeFlowValid: true);
    }
}
