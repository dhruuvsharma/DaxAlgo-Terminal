using FluentAssertions;
using TradingTerminal.Core.Ml;
using Xunit;

namespace TradingTerminal.Tests.Ml;

public sealed class OrderBookMicroPredictorTests
{
    private const double Tick = 0.25;
    private static readonly DateTime T0 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private static OrderBookStepSummary Step(
        int index, double microprice, double imbalanceL1 = 0.8, double spreadTicks = 1.0,
        long bid3 = 300, long ask3 = 300, double sweepBuy = 0.25, double sweepSell = 0.25,
        long flow = 50, int trades = 5, bool flowValid = true)
    {
        var halfSpread = spreadTicks * Tick / 2.0;
        return new OrderBookStepSummary(
            TimestampUtc: T0.AddMilliseconds(250.0 * index),
            BestBid: microprice - halfSpread,
            BestAsk: microprice + halfSpread,
            BestBidSize: 180, BestAskSize: 20,
            Microprice: microprice, WeightedMid: microprice,
            ImbalanceL1: imbalanceL1, ImbalanceCum: imbalanceL1,
            BidDepthTop3: bid3, AskDepthTop3: ask3,
            BidDepthTopN: 1_000, AskDepthTopN: 1_000,
            LargestGapBid: Tick, LargestGapAsk: Tick, MinLevelGap: Tick,
            SweepCostBuy: sweepBuy, SweepCostSell: sweepSell,
            SignedTradeFlow: flow, TradeCount: trades, TradeFlowValid: flowValid);
    }

    private static OrderBookStepSummary UnusableStep(int index) =>
        Step(index, microprice: 0) with { BestBid = 0, BestAsk = 0 };

    [Fact]
    public void LearnsAConstantDrift_AndBeatsTheImbalanceBaselineOnMae()
    {
        var ml = new OrderBookMicroPredictor();
        OrderBookForecast? forecast = null;
        for (var i = 0; i < 300; i++)
            forecast = ml.OnStep(Step(i, 100.0 + i * Tick));

        ml.IsReady.Should().BeTrue();
        ml.TickSize.Should().Be(Tick);
        forecast.Should().NotBeNull();

        var lastMicro = 100.0 + 299 * Tick;
        // Flagship horizon 4 (index 2 in {1,2,4,8,20}): the drift realizes +4 ticks.
        var flagship = forecast!.Path.Single(p => p.HorizonSteps == 4);
        flagship.Microprice.Should().BeApproximately(lastMicro + 4 * Tick, 0.5 * Tick);

        var mlAcc = ml.MlAccuracy;
        var baseAcc = ml.BaselineAccuracy;
        mlAcc.ScoredCount.Should().BeGreaterThan(100);
        mlAcc.DirectionalHitRate.Should().BeGreaterThanOrEqualTo(0.9);
        baseAcc.DirectionalHitRate.Should().Be(1.0, "the imbalance rule calls the right direction on a monotone drift");
        mlAcc.PocMaeTicks.Should().BeLessThan(baseAcc.PocMaeTicks,
            "the model learns the drift magnitude; the rule only ever predicts a half-spread move");
    }

    [Fact]
    public void EventProbability_LearnsAPredictableSpreadPattern()
    {
        // Spread jumps to 5 ticks every 4th step, so every event window (8 steps) from a
        // 1-tick-spread reference contains a widening. Base rate ≈ 0.75 (wide-spread reference
        // steps don't re-fire: their +1-tick threshold is above the future max).
        var ml = new OrderBookMicroPredictor();
        OrderBookForecast? forecast = null;
        for (var i = 0; i < 402; i++)
        {
            forecast = ml.OnStep(Step(i, 100.0, spreadTicks: i % 4 == 0 ? 5.0 : 1.0));
            if (forecast is not null)
            {
                forecast.PSpreadWidens.Should().BeInRange(0.0, 1.0);
                forecast.PDepthDrains.Should().BeInRange(0.0, 1.0);
                forecast.PSweepJumps.Should().BeInRange(0.0, 1.0);
            }
        }

        // Final step index 401 (401 % 4 == 1 → narrow-spread reference): widening ahead is certain.
        forecast.Should().NotBeNull();
        forecast!.PSpreadWidens.Should().BeGreaterThan(0.6);
        ml.SpreadWidenScore.ScoredCount.Should().BeGreaterThan(100);
        ml.SpreadWidenScore.BaseRate.Should().BeApproximately(0.75, 0.1);
    }

    [Fact]
    public void EventProbability_StaysLowWhenTheEventNeverFires()
    {
        var ml = new OrderBookMicroPredictor();
        OrderBookForecast? forecast = null;
        for (var i = 0; i < 300; i++)
            forecast = ml.OnStep(Step(i, 100.0));

        forecast.Should().NotBeNull();
        forecast!.PSpreadWidens.Should().BeLessThan(0.2);
        forecast.PDepthDrains.Should().BeLessThan(0.2);
        forecast.PSweepJumps.Should().BeLessThan(0.2);
        ml.SpreadWidenScore.BaseRate.Should().Be(0.0);
    }

    [Fact]
    public void NotReadyGate_ReturnsNullEarly()
    {
        var ml = new OrderBookMicroPredictor();
        for (var i = 0; i < 20; i++)
            ml.OnStep(Step(i, 100.0 + i * Tick)).Should().BeNull("fewer than MinSamplesReady flagship updates");
        ml.IsReady.Should().BeFalse();
        ml.SamplesSeen.Should().Be(20);
        ml.LastForecast.Should().BeNull();
    }

    [Fact]
    public void IsDeterministic_TwoEnginesSameFeedSameOutputs()
    {
        var a = new OrderBookMicroPredictor();
        var b = new OrderBookMicroPredictor();
        var rng = new Random(17);

        OrderBookForecast? fa = null, fb = null;
        var micro = 100.0;
        for (var i = 0; i < 400; i++)
        {
            micro += rng.Next(-2, 3) * Tick;
            var step = Step(i, micro,
                imbalanceL1: rng.NextDouble() * 2 - 1,
                spreadTicks: rng.Next(1, 4),
                bid3: 100 + rng.Next(0, 400), ask3: 100 + rng.Next(0, 400),
                sweepBuy: rng.Next(0, 4) * Tick, sweepSell: rng.Next(0, 4) * Tick,
                flow: rng.Next(-200, 200), trades: rng.Next(0, 20));
            fa = a.OnStep(step);
            fb = b.OnStep(step);
        }

        (fa is null).Should().Be(fb is null);
        if (fa is not null && fb is not null)
        {
            fa.ReferenceMicroprice.Should().Be(fb.ReferenceMicroprice);
            fa.PSpreadWidens.Should().Be(fb.PSpreadWidens);
            fa.PDepthDrains.Should().Be(fb.PDepthDrains);
            fa.PSweepJumps.Should().Be(fb.PSweepJumps);
            fa.Path.Count.Should().Be(fb.Path.Count);
            for (var i = 0; i < fa.Path.Count; i++)
                fa.Path[i].Microprice.Should().Be(fb.Path[i].Microprice);
        }
    }

    [Fact]
    public void UnusableSteps_DropPendingsAndNeverLeakNaN()
    {
        var ml = new OrderBookMicroPredictor();
        var rng = new Random(5);
        var micro = 50.0;
        OrderBookForecast? forecast = null;
        for (var i = 0; i < 500; i++)
        {
            micro += rng.Next(-1, 2) * Tick;
            forecast = rng.NextDouble() < 0.08
                ? ml.OnStep(UnusableStep(i))
                : ml.OnStep(Step(i, micro, imbalanceL1: rng.NextDouble() * 2 - 1));

            if (forecast is null) continue;
            double.IsFinite(forecast.ReferenceMicroprice).Should().BeTrue();
            double.IsFinite(forecast.PSpreadWidens).Should().BeTrue();
            double.IsFinite(forecast.PDepthDrains).Should().BeTrue();
            double.IsFinite(forecast.PSweepJumps).Should().BeTrue();
            foreach (var p in forecast.Path) double.IsFinite(p.Microprice).Should().BeTrue();
        }
        ml.IsReady.Should().BeTrue("usable steps vastly outnumber unusable ones");
    }

    [Fact]
    public void TickEstimate_RefinesDownwardAndPinsPendings()
    {
        var ml = new OrderBookMicroPredictor();
        // First 50 steps report a coarse 0.50 gap, then the true 0.25 grid shows up.
        for (var i = 0; i < 50; i++)
            ml.OnStep(Step(i, 100.0 + i * Tick) with { MinLevelGap = 0.50 });
        ml.TickSize.Should().Be(0.50);

        OrderBookForecast? forecast = null;
        for (var i = 50; i < 300; i++)
            forecast = ml.OnStep(Step(i, 100.0 + i * Tick));

        ml.TickSize.Should().Be(0.25, "the estimate is the running minimum of observed gaps");
        forecast.Should().NotBeNull("refinement mid-run must not corrupt queued targets");
        forecast!.TickSize.Should().Be(0.25);
        foreach (var p in forecast.Path) double.IsFinite(p.Microprice).Should().BeTrue();
    }

    [Fact]
    public void ResetClearsAllState()
    {
        var ml = new OrderBookMicroPredictor();
        for (var i = 0; i < 200; i++)
            ml.OnStep(Step(i, 100.0 + i * Tick));
        ml.IsReady.Should().BeTrue();

        ml.Reset();

        ml.IsReady.Should().BeFalse();
        ml.SamplesSeen.Should().Be(0);
        ml.LastForecast.Should().BeNull();
        ml.TickSize.Should().Be(0.01, "the tick estimate resets to the default");
        ml.MlAccuracy.ScoredCount.Should().Be(0);
        ml.BaselineAccuracy.ScoredCount.Should().Be(0);
        ml.SpreadWidenScore.ScoredCount.Should().Be(0);
        ml.OnStep(Step(0, 100.0)).Should().BeNull();
    }
}
