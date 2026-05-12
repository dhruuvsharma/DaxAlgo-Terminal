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
}
