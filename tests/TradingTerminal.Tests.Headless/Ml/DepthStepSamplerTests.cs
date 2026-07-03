using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Ml;
using Xunit;

namespace TradingTerminal.Tests.Ml;

public sealed class DepthStepSamplerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);

    private static DepthSnapshot Snap(double offsetMs, double bid = 100.00, double ask = 100.25) =>
        new(T0.AddMilliseconds(offsetMs),
            Bids: new[] { new DepthLevel(bid, 500), new DepthLevel(bid - 0.25, 300) },
            Asks: new[] { new DepthLevel(ask, 400), new DepthLevel(ask + 0.25, 200) });

    private static DepthStepSampler NewSampler() => new(Step, statsDepth: 10, sweepSize: 1_000);

    [Fact]
    public void FirstSnapshotEmitsNothing()
    {
        var output = new List<OrderBookStepSummary>();
        NewSampler().Add(Snap(10), output).Should().Be(0);
        output.Should().BeEmpty();
    }

    [Fact]
    public void EmitsOneLocfSummaryPerCrossedBoundary()
    {
        var sampler = NewSampler();
        var output = new List<OrderBookStepSummary>();
        sampler.Add(Snap(10, bid: 100.00), output);
        // 10 ms → 1_010 ms crosses boundaries at 250/500/750/1000 — all carried from the 10 ms book.
        sampler.Add(Snap(1_010, bid: 200.00), output).Should().Be(4);

        output.Should().HaveCount(4);
        output.Select(s => s.TimestampUtc).Should().Equal(
            T0.AddMilliseconds(250), T0.AddMilliseconds(500), T0.AddMilliseconds(750), T0.AddMilliseconds(1000));
        output.Should().OnlyContain(s => s.BestBid == 100.00, "LOCF: the boundary state is the snapshot before it");
        output.Should().OnlyContain(s => !s.TradeFlowValid && s.SignedTradeFlow == 0 && s.TradeCount == 0);
        sampler.LastBoundaryUtc.Should().Be(T0.AddMilliseconds(1000));
    }

    [Fact]
    public void EachBoundaryIsEmittedExactlyOnce()
    {
        var sampler = NewSampler();
        var output = new List<OrderBookStepSummary>();
        sampler.Add(Snap(100), output);
        sampler.Add(Snap(200), output);   // no boundary in (100, 200]
        sampler.Add(Snap(260), output);   // boundary 250, carried from the 200 ms snapshot
        sampler.Add(Snap(510), output);   // boundary 500, carried from the 260 ms snapshot

        output.Should().HaveCount(2);
        output[0].TimestampUtc.Should().Be(T0.AddMilliseconds(250));
        output[1].TimestampUtc.Should().Be(T0.AddMilliseconds(500));
    }

    [Fact]
    public void GapsBeyondMaxGapEmitNothing()
    {
        var sampler = new DepthStepSampler(Step, 10, 1_000, maxGap: TimeSpan.FromSeconds(5));
        var output = new List<OrderBookStepSummary>();
        sampler.Add(Snap(0), output);
        sampler.Add(Snap(10_000), output).Should().Be(0, "a 10 s recording gap must not train as a frozen book");
        // Sampling resumes cleanly after the gap.
        sampler.Add(Snap(10_600), output).Should().Be(2); // boundaries 10_250, 10_500
    }

    [Fact]
    public void SummaryFieldsMatchTheMicrostructureMath()
    {
        var sampler = NewSampler();
        var output = new List<OrderBookStepSummary>();
        sampler.Add(Snap(10), output);
        sampler.Add(Snap(300), output);

        var s = output.Single();
        s.BestBid.Should().Be(100.00);
        s.BestAsk.Should().Be(100.25);
        s.BestBidSize.Should().Be(500);
        s.BestAskSize.Should().Be(400);
        s.ImbalanceL1.Should().BeApproximately((500.0 - 400.0) / 900.0, 1e-12);
        s.BidDepthTop3.Should().Be(800);
        s.AskDepthTop3.Should().Be(600);
        s.MinLevelGap.Should().BeApproximately(0.25, 1e-12);
        // Sweep 1000 through asks (400@100.25 + 200@100.50, book short): avg fill − touch.
        var expectedBuyCost = (400 * 100.25 + 200 * 100.50) / 600.0 - 100.25;
        s.SweepCostBuy.Should().BeApproximately(expectedBuyCost, 1e-9);
    }

    [Fact]
    public void ResetForgetsThePreviousSnapshotAndWatermark()
    {
        var sampler = NewSampler();
        var output = new List<OrderBookStepSummary>();
        sampler.Add(Snap(10), output);
        sampler.Add(Snap(300), output);
        sampler.LastBoundaryUtc.Should().NotBe(DateTime.MinValue);

        sampler.Reset();

        sampler.LastBoundaryUtc.Should().Be(DateTime.MinValue);
        output.Clear();
        sampler.Add(Snap(1_000), output).Should().Be(0, "the first snapshot after reset primes, never emits");
    }
}
