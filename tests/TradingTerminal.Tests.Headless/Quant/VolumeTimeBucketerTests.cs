using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using Xunit;

namespace TradingTerminal.Tests.Quant;

public sealed class VolumeTimeBucketerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 9, 30, 0, DateTimeKind.Utc);

    private static FootprintPrint P(double price, long size, AggressorSide side, int secs)
        => new(price, size, side, T0.AddSeconds(secs));

    [Fact]
    public void ExactBoundaries_ProduceCompleteBuckets()
    {
        // Bucket size 100. Two prints of 50 each close the first bucket exactly.
        var prints = new[]
        {
            P(100, 50, AggressorSide.Buy, 0),
            P(100, 50, AggressorSide.Sell, 1),
            P(100, 50, AggressorSide.Buy, 2),
            P(100, 50, AggressorSide.Sell, 3),
        };

        var buckets = VolumeTimeBucketer.Bucketize(prints, bucketVolume: 100).ToList();

        buckets.Should().HaveCount(2);
        buckets[0].TotalVolume.Should().Be(100);
        buckets[1].TotalVolume.Should().Be(100);
        buckets[0].BuyVolume.Should().Be(50);
        buckets[0].SellVolume.Should().Be(50);
    }

    [Fact]
    public void SpanningPrint_IsNotSplit_ClosingBucketKeepsTheWholePrint()
    {
        // Bucket size 100. First print 60, then a 70 that overflows: per the implementation the
        // print is NOT split — the bucket closes carrying the whole 70, so it totals 130, and the
        // next bucket starts empty.
        var prints = new[]
        {
            P(100, 60, AggressorSide.Buy, 0),
            P(100, 70, AggressorSide.Buy, 1), // 60+70 = 130 ≥ 100 ⇒ close with 130
            P(100, 40, AggressorSide.Sell, 2),
            P(100, 65, AggressorSide.Sell, 3), // 40+65 = 105 ≥ 100 ⇒ close with 105
        };

        var buckets = VolumeTimeBucketer.Bucketize(prints, bucketVolume: 100).ToList();

        buckets.Should().HaveCount(2);
        buckets[0].TotalVolume.Should().Be(130); // whole spanning print kept, no carry
        buckets[1].TotalVolume.Should().Be(105);
    }

    [Fact]
    public void BuyFraction_AndVwap_HandChecked()
    {
        // Bucket: buy 30 @ 100, sell 10 @ 110, buy 60 @ 100 ⇒ total 100, closes.
        // BuyVol = 90, SellVol = 10 ⇒ BuyFraction = 0.9.
        // VWAP = (30*100 + 10*110 + 60*100) / 100 = (3000 + 1100 + 6000)/100 = 10100/100 = 101.
        var prints = new[]
        {
            P(100, 30, AggressorSide.Buy, 0),
            P(110, 10, AggressorSide.Sell, 1),
            P(100, 60, AggressorSide.Buy, 2),
        };

        var buckets = VolumeTimeBucketer.Bucketize(prints, bucketVolume: 100).ToList();

        buckets.Should().HaveCount(1);
        buckets[0].BuyFraction.Should().BeApproximately(0.9, 1e-12);
        buckets[0].Vwap.Should().BeApproximately(101.0, 1e-12);
        buckets[0].Delta.Should().Be(80);
    }

    [Fact]
    public void PartialBucket_DroppedByDefault_EmittedWhenRequested()
    {
        var prints = new[]
        {
            P(100, 100, AggressorSide.Buy, 0), // closes bucket 1
            P(100, 30, AggressorSide.Buy, 1),  // partial — under target
        };

        VolumeTimeBucketer.Bucketize(prints, 100, includePartial: false).Should().HaveCount(1);

        var withPartial = VolumeTimeBucketer.Bucketize(prints, 100, includePartial: true).ToList();
        withPartial.Should().HaveCount(2);
        withPartial[1].TotalVolume.Should().Be(30);
    }

    [Fact]
    public void StartAndEndTimes_TrackFirstAndClosingPrints()
    {
        var prints = new[]
        {
            P(100, 40, AggressorSide.Buy, 5),
            P(100, 60, AggressorSide.Buy, 9), // closing print
        };

        var bucket = VolumeTimeBucketer.Bucketize(prints, 100).Single();
        bucket.StartUtc.Should().Be(T0.AddSeconds(5));
        bucket.EndUtc.Should().Be(T0.AddSeconds(9));
    }

    [Fact]
    public void NonPositiveSizePrints_AreSkipped()
    {
        var prints = new[]
        {
            P(100, 0, AggressorSide.Buy, 0),
            P(100, 100, AggressorSide.Buy, 1),
        };
        var buckets = VolumeTimeBucketer.Bucketize(prints, 100).ToList();
        buckets.Should().HaveCount(1);
        buckets[0].TotalVolume.Should().Be(100);
    }

    [Fact]
    public void Bucketize_NonPositiveBucketVolume_Throws()
    {
        var act = () => VolumeTimeBucketer.Bucketize(Array.Empty<FootprintPrint>(), 0).ToList();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── bucket-size heuristics ──────────────────────────────────────────────────────

    [Fact]
    public void AdaptiveBucketVolume_IsMedianOverFour()
    {
        // Median of {100,200,300,400,500} = 300 ⇒ 300/4 = 75.
        VolumeTimeBucketer.AdaptiveBucketVolume(new long[] { 100, 200, 300, 400, 500 }).Should().Be(75);
        // Even count: median of {100,300} = 200 ⇒ 200/4 = 50.
        VolumeTimeBucketer.AdaptiveBucketVolume(new long[] { 100, 300 }).Should().Be(50);
    }

    [Fact]
    public void AdaptiveBucketVolume_RobustToFatBar_AndAtLeastOne()
    {
        // One huge outlier doesn't move the median.
        VolumeTimeBucketer.AdaptiveBucketVolume(new long[] { 100, 100, 100, 1_000_000 })
            .Should().Be(25); // median of sorted {100,100,100,1e6} = (100+100)/2 = 100 ⇒ 25
        VolumeTimeBucketer.AdaptiveBucketVolume(new long[] { 1 }).Should().Be(1);
        VolumeTimeBucketer.AdaptiveBucketVolume(Array.Empty<long>()).Should().Be(1);
    }

    [Fact]
    public void VpinBucketVolume_IsDailyOverFifty()
    {
        VolumeTimeBucketer.VpinBucketVolume(5000).Should().Be(100); // 5000/50
        VolumeTimeBucketer.VpinBucketVolume(50).Should().Be(1);     // 50/50
        VolumeTimeBucketer.VpinBucketVolume(0).Should().Be(1);      // guard
        VolumeTimeBucketer.VpinBucketVolume(-10).Should().Be(1);
    }
}
