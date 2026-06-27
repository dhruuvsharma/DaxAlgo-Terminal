using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

public sealed class MicrostructureTests
{
    [Fact]
    public void Microprice_LeansTowardThinnerSide()
    {
        // Bid 99.95 with size 10, Ask 100.05 with size 100. Ask is thicker, so the next
        // move is more likely down → microprice should be below the mid.
        var mp = Microstructure.Microprice(bid: 99.95, ask: 100.05, bidSize: 10, askSize: 100);
        mp.Should().BeLessThan(100.00);
        mp.Should().BeGreaterThan(99.95);
    }

    [Fact]
    public void Microprice_EqualsMidWhenSizesEqual()
    {
        Microstructure.Microprice(99.95, 100.05, 50, 50).Should().BeApproximately(100.00, 1e-12);
    }

    [Fact]
    public void Microprice_FallsBackToMidWhenSizesZero()
    {
        Microstructure.Microprice(99.5, 100.5, 0, 0).Should().BeApproximately(100.0, 1e-12);
    }

    [Fact]
    public void QueueImbalance_IsBoundedAndSigned()
    {
        Microstructure.QueueImbalance(100, 0).Should().Be(1.0);
        Microstructure.QueueImbalance(0, 100).Should().Be(-1.0);
        Microstructure.QueueImbalance(50, 50).Should().Be(0.0);
        Microstructure.QueueImbalance(75, 25).Should().BeApproximately(0.5, 1e-12);
    }

    [Fact]
    public void TickOverloads_AgreeWithRawValues()
    {
        var tick = new Tick(DateTime.UtcNow, Bid: 99.95, Ask: 100.05, BidSize: 30, AskSize: 70);
        Microstructure.Microprice(tick).Should()
            .BeApproximately(Microstructure.Microprice(99.95, 100.05, 30, 70), 1e-12);
        Microstructure.HalfSpread(tick).Should().BeApproximately(0.05, 1e-12);
    }

    // ── L2 / depth helpers ────────────────────────────────────────────────────────────

    private static DepthSnapshot Book(
        (double Price, long Size)[] bids,
        (double Price, long Size)[] asks)
        => new(
            DateTime.UtcNow,
            bids.Select(b => new DepthLevel(b.Price, b.Size)).ToList(),
            asks.Select(a => new DepthLevel(a.Price, a.Size)).ToList());

    [Fact]
    public void CumulativeImbalance_IsBoundedAndSigned()
    {
        var heavyBid = Book(
            bids: [(99.95, 100), (99.94, 200), (99.93, 200)],
            asks: [(100.05, 10), (100.06, 20), (100.07, 30)]);
        var ci = Microstructure.CumulativeImbalance(heavyBid, depthLevels: 5);
        ci.Should().BeGreaterThan(0.5);

        var heavyAsk = Book(
            bids: [(99.95, 10)],
            asks: [(100.05, 200), (100.06, 200), (100.07, 100)]);
        Microstructure.CumulativeImbalance(heavyAsk).Should().BeLessThan(-0.5);

        var empty = new DepthSnapshot(DateTime.UtcNow, Array.Empty<DepthLevel>(), Array.Empty<DepthLevel>());
        Microstructure.CumulativeImbalance(empty).Should().Be(0);
    }

    [Fact]
    public void WeightedMidPrice_LeansTowardHeavySide()
    {
        var heavyBid = Book(
            bids: [(99.95, 1000), (99.94, 500)],
            asks: [(100.05, 10), (100.06, 5)]);
        var simpleMid = (heavyBid.BestBid + heavyBid.BestAsk) * 0.5;
        var weighted = Microstructure.WeightedMidPrice(heavyBid);
        weighted.Should().BeLessThan(simpleMid, "size-weighted mid pulls toward the heavier side's price stack");
    }

    [Fact]
    public void EstimatedSlippage_WalksLevels()
    {
        var asks = new[]
        {
            new DepthLevel(100.05, 10),
            new DepthLevel(100.10, 10),
            new DepthLevel(100.20, 30),
        };
        // Buy 25 units: 10 @ 100.05, 10 @ 100.10, 5 @ 100.20
        // avg = (10*100.05 + 10*100.10 + 5*100.20) / 25 = (1000.5 + 1001.0 + 501.0)/25 = 2502.5/25 = 100.10
        // touch = 100.05 → slippage = 0.05
        var slip = Microstructure.EstimatedSlippage(asks, quantity: 25, out var filled);
        filled.Should().BeTrue();
        slip.Should().BeApproximately(0.05, 1e-9);
    }

    [Fact]
    public void EstimatedSlippage_FlagsInsufficientLiquidity()
    {
        var asks = new[] { new DepthLevel(100.05, 5) };
        var _ = Microstructure.EstimatedSlippage(asks, quantity: 100, out var filled);
        filled.Should().BeFalse();
    }

    [Fact]
    public void LargestLevelGap_PicksTheBiggestStep()
    {
        var asks = new[]
        {
            new DepthLevel(100.00, 10),
            new DepthLevel(100.01, 10),
            new DepthLevel(100.50, 10), // big gap here
            new DepthLevel(100.51, 10),
        };
        Microstructure.LargestLevelGap(asks).Should().BeApproximately(0.49, 1e-9);
    }
}
