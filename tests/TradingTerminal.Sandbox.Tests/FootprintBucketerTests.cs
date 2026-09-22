using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using Xunit;

namespace TradingTerminal.Sandbox.Tests;

/// <summary>Bookmap-parity footprint bar kinds: time / reversal / range / volume + bar stats.</summary>
public sealed class FootprintBucketerTests
{
    private const double Tick = 0.25;
    private static readonly DateTime T0 = new(2026, 1, 1, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TimeBucketer_SealsOnBucketRoll()
    {
        var b = FootprintBucketerFactory.Create(
            FootprintBarKind.Time, Tick, FeedQuality.RealTape, TimeSpan.FromMinutes(1));

        Assert.Null(b.Add(Buy(T0, 100, 10)));
        Assert.Null(b.Add(Sell(T0.AddSeconds(30), 100.25, 5)));
        var sealedBar = b.Add(Buy(T0.AddMinutes(1), 100.5, 8));
        Assert.NotNull(sealedBar);
        Assert.Equal(5, sealedBar!.Delta); // 10 - 5
        Assert.Equal(10, sealedBar.MaxDelta); // peak running delta after the buy
        Assert.True(sealedBar.AskTrades >= 1);
        Assert.True(sealedBar.BidTrades >= 1);
    }

    [Fact]
    public void ReversalBucketer_SealsAfterReverseFromHigh()
    {
        var b = FootprintBucketerFactory.Create(
            FootprintBarKind.Reversal, Tick, FeedQuality.RealTape, threshold: 4);

        // Climb 1.00 (4 ticks), then reverse down 1.00 → seal.
        Assert.Null(b.Add(Buy(T0, 100.00, 1)));
        Assert.Null(b.Add(Buy(T0.AddSeconds(1), 101.00, 1)));
        var sealedBar = b.Add(Sell(T0.AddSeconds(2), 100.00, 1));
        Assert.NotNull(sealedBar);
        Assert.Equal(2, sealedBar!.AskTrades); // two buys in sealed bar; sell opens next
    }

    [Fact]
    public void RangeBucketer_SealsWhenHighLowExceedsTicks()
    {
        var b = FootprintBucketerFactory.Create(
            FootprintBarKind.Range, Tick, FeedQuality.RealTape, threshold: 4);

        Assert.Null(b.Add(Buy(T0, 100.00, 1)));
        // 100 → 101 = 4 ticks → seal including this print
        var sealedBar = b.Add(Buy(T0.AddSeconds(1), 101.00, 1));
        Assert.NotNull(sealedBar);
        Assert.Equal(5, sealedBar!.BarHeightTicks); // 100,100.25,...,101 inclusive ≈ 5
    }

    [Fact]
    public void VolumeBucketer_SealsAtThreshold()
    {
        var b = FootprintBucketerFactory.Create(
            FootprintBarKind.Volume, Tick, FeedQuality.RealTape, threshold: 100);

        Assert.Null(b.Add(Buy(T0, 100, 40)));
        Assert.Null(b.Add(Buy(T0.AddSeconds(1), 100, 40)));
        var sealedBar = b.Add(Buy(T0.AddSeconds(2), 100, 30));
        Assert.NotNull(sealedBar);
        Assert.Equal(110, sealedBar!.TotalVolume);
    }

    [Fact]
    public void BuildBar_ComputesPullbackDeltaFromRunningExtremes()
    {
        // +50, then pull back to +20 → pullback = 20 - 50 = -30
        var prints = new[]
        {
            Buy(T0, 100, 50),
            Sell(T0.AddSeconds(1), 100, 30),
        };
        var bar = FootprintFeatures.BuildBar(prints, Tick, T0, T0.AddMinutes(1), FeedQuality.RealTape);
        Assert.Equal(50, bar.MaxDelta);
        Assert.Equal(20, bar.Delta);
        Assert.Equal(-30, bar.PullbackDelta);
    }

    private static FootprintPrint Buy(DateTime t, double px, long sz) =>
        new(px, sz, AggressorSide.Buy, t);

    private static FootprintPrint Sell(DateTime t, double px, long sz) =>
        new(px, sz, AggressorSide.Sell, t);
}
