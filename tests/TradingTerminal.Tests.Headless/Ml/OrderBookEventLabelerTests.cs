using FluentAssertions;
using TradingTerminal.Core.Ml;
using Xunit;

namespace TradingTerminal.Tests.Ml;

public sealed class OrderBookEventLabelerTests
{
    private const double Tick = 0.25;

    [Theory]
    [InlineData(0.25, 0.50, true)]   // widened by exactly 1 tick → fires
    [InlineData(0.25, 0.49, false)]  // just under the threshold
    [InlineData(0.25, 0.25, false)]  // unchanged
    [InlineData(0.50, 0.75, true)]   // absolute threshold — works from any reference spread
    public void SpreadWidened_UsesAbsoluteTickThreshold(double refSpread, double maxFuture, bool expected)
        => OrderBookEventLabeler.SpreadWidened(refSpread, maxFuture, Tick).Should().Be(expected);

    [Theory]
    [InlineData(1000, 1000, 700, 1000, true)]   // bid side dipped to exactly the 70% threshold
    [InlineData(1000, 1000, 701, 1000, false)]  // just above it
    [InlineData(1000, 1000, 1000, 699, true)]   // either-side semantics: ask side fires alone
    [InlineData(1000, 1000, 1000, 1000, false)] // no dip
    public void DepthDrained_FiresOnEitherSide(long refBid, long refAsk, long minBid, long minAsk, bool expected)
        => OrderBookEventLabeler.DepthDrained(refBid, refAsk, minBid, minAsk).Should().Be(expected);

    [Fact]
    public void DepthDrained_IgnoresAnEmptyReferenceSide()
    {
        OrderBookEventLabeler.DepthDrained(0, 1000, 0, 1000).Should().BeFalse(
            "a side that was already empty at reference can't 'drain'");
    }

    [Theory]
    [InlineData(1.0, 1.25, true)]    // exactly 1.25× → fires
    [InlineData(1.0, 1.24, false)]
    [InlineData(0.0, 0.3125, true)]  // tick floor: ref 0 → threshold 1.25 × tick = 0.3125
    [InlineData(0.0, 0.31, false)]
    public void SweepJumped_UsesTickFloorOnReference(double refSweep, double maxFuture, bool expected)
        => OrderBookEventLabeler.SweepJumped(refSweep, maxFuture, Tick).Should().Be(expected);
}
