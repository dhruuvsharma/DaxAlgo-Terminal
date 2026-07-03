using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

public sealed class FootprintTimeBucketerTests
{
    private static readonly TimeSpan Span = TimeSpan.FromSeconds(15);
    private const double Tick = 0.25;

    private static readonly DateTime T0 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private static FootprintPrint Print(DateTime time, double price, long size, AggressorSide side) =>
        new(price, size, side, time);

    private static FootprintTimeBucketer NewBucketer() => new(Span, Tick, FeedQuality.RealTape);

    [Fact]
    public void FirstPrintNeverSeals_AndOpensTheSnappedBucket()
    {
        var b = NewBucketer();
        var sealedBar = b.Add(Print(T0.AddSeconds(7), 100.0, 5, AggressorSide.Buy));

        sealedBar.Should().BeNull();
        b.CurrentBucketStart.Should().Be(T0, "bucket start snaps to ts − ts mod span");
        b.CumulativeDelta.Should().Be(0);
    }

    [Fact]
    public void SealsExactlyOnBucketRoll_WithEndAtTheNewBucketStart()
    {
        var b = NewBucketer();
        b.Add(Print(T0.AddSeconds(1), 100.00, 10, AggressorSide.Buy));
        b.Add(Print(T0.AddSeconds(9), 100.25, 4, AggressorSide.Sell));

        var sealedBar = b.Add(Print(T0.AddSeconds(16), 100.50, 3, AggressorSide.Buy));

        sealedBar.Should().NotBeNull();
        sealedBar!.StartUtc.Should().Be(T0);
        sealedBar.EndUtc.Should().Be(T0.AddSeconds(15), "the sealed bar ends where the new bucket starts");
        sealedBar.BuyVolume.Should().Be(10);
        sealedBar.SellVolume.Should().Be(4);
        sealedBar.Delta.Should().Be(6);
        b.CurrentBucketStart.Should().Be(T0.AddSeconds(15));
    }

    [Fact]
    public void CumulativeDeltaThreadsAcrossSealedBars()
    {
        var b = NewBucketer();
        b.Add(Print(T0.AddSeconds(1), 100.0, 10, AggressorSide.Buy));          // bar 1: delta +10
        b.Add(Print(T0.AddSeconds(16), 100.0, 3, AggressorSide.Sell));         // seals bar 1; bar 2: delta -3
        var second = b.Add(Print(T0.AddSeconds(31), 100.0, 1, AggressorSide.Buy)); // seals bar 2

        second.Should().NotBeNull();
        second!.CumulativeDelta.Should().Be(10 - 3);
        b.CumulativeDelta.Should().Be(7);
    }

    [Fact]
    public void BuildForming_MatchesDirectBuildBarOverTheSamePrints()
    {
        var b = NewBucketer();
        var prints = new[]
        {
            Print(T0.AddSeconds(2), 100.00, 7, AggressorSide.Buy),
            Print(T0.AddSeconds(5), 100.25, 2, AggressorSide.Sell),
            Print(T0.AddSeconds(9), 100.00, 4, AggressorSide.Unknown),
        };
        foreach (var p in prints) b.Add(p);

        var forming = b.BuildForming();
        var expected = FootprintFeatures.BuildBar(prints, Tick, T0, T0 + Span, FeedQuality.RealTape);

        forming.Should().NotBeNull();
        forming!.StartUtc.Should().Be(expected.StartUtc);
        forming.EndUtc.Should().Be(expected.EndUtc);
        forming.PocPrice.Should().Be(expected.PocPrice);
        forming.BuyVolume.Should().Be(expected.BuyVolume);
        forming.SellVolume.Should().Be(expected.SellVolume);
        forming.Delta.Should().Be(expected.Delta);
        forming.CumulativeDelta.Should().Be(expected.CumulativeDelta);
        forming.Rows.Should().HaveSameCount(expected.Rows);
    }

    [Fact]
    public void BuildForming_ReturnsNullBeforeAnyPrint()
    {
        NewBucketer().BuildForming().Should().BeNull();
    }

    [Fact]
    public void ResetHonoursTheCumulativeDeltaSeed()
    {
        var b = NewBucketer();
        b.Add(Print(T0.AddSeconds(1), 100.0, 10, AggressorSide.Buy));
        b.Add(Print(T0.AddSeconds(16), 100.0, 1, AggressorSide.Buy));
        b.CumulativeDelta.Should().Be(10);

        b.Reset(cumulativeDeltaSeed: 42);

        b.CumulativeDelta.Should().Be(42);
        b.CurrentBucketStart.Should().Be(DateTime.MinValue);
        b.BuildForming().Should().BeNull();

        b.Add(Print(T0.AddSeconds(1), 100.0, 5, AggressorSide.Buy));
        var sealedBar = b.Add(Print(T0.AddSeconds(16), 100.0, 1, AggressorSide.Buy));
        sealedBar!.CumulativeDelta.Should().Be(42 + 5, "the seed threads into the next sealed bar");
    }

    [Fact]
    public void OutOfOrderPrintIntoAnOlderBucket_StillSeals()
    {
        // Mirrors the original inline window logic: any bucket CHANGE seals, including a
        // regression to an older bucket (e.g. clock skew across brokers).
        var b = NewBucketer();
        b.Add(Print(T0.AddSeconds(16), 100.0, 10, AggressorSide.Buy));
        var sealedBar = b.Add(Print(T0.AddSeconds(5), 100.0, 2, AggressorSide.Sell));

        sealedBar.Should().NotBeNull();
        sealedBar!.StartUtc.Should().Be(T0.AddSeconds(15));
        b.CurrentBucketStart.Should().Be(T0);
    }
}
