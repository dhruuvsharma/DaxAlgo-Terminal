using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData.AdvancedRegime;
using Xunit;

namespace TradingTerminal.Tests.Regime;

public sealed class BarTimeframeAggregatorTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc);

    private static List<Bar> OneMinuteBars(int count, DateTime start)
    {
        var bars = new List<Bar>(count);
        for (var i = 0; i < count; i++)
            bars.Add(new Bar(start.AddMinutes(i), Open: i, High: i + 0.5, Low: i - 0.5, Close: i + 0.25, Volume: 10));
        return bars;
    }

    [Fact]
    public void Aggregates_1m_to_5m_with_correct_ohlcv()
    {
        var result = BarTimeframeAggregator.Aggregate(OneMinuteBars(10, T0), TimeSpan.FromMinutes(5));

        result.Should().HaveCount(2);

        result[0].TimestampUtc.Should().Be(T0);
        result[0].Open.Should().Be(0);
        result[0].High.Should().Be(4.5);
        result[0].Low.Should().Be(-0.5);
        result[0].Close.Should().Be(4.25);
        result[0].Volume.Should().Be(50);

        result[1].TimestampUtc.Should().Be(T0.AddMinutes(5));
        result[1].Open.Should().Be(5);
        result[1].High.Should().Be(9.5);
        result[1].Low.Should().Be(4.5);
        result[1].Close.Should().Be(9.25);
        result[1].Volume.Should().Be(50);
    }

    [Fact]
    public void Floors_to_20m_boundaries_even_when_series_starts_mid_bucket()
    {
        // 20 one-minute bars starting 10:07 → buckets 10:00 (10:07-10:19) and 10:20 (10:20-10:26).
        var bars = OneMinuteBars(20, T0.AddMinutes(7));
        var result = BarTimeframeAggregator.Aggregate(bars, TimeSpan.FromMinutes(20));

        result.Should().HaveCount(2);
        result[0].TimestampUtc.Should().Be(T0);
        result[0].Volume.Should().Be(130);
        result[1].TimestampUtc.Should().Be(T0.AddMinutes(20));
        result[1].Volume.Should().Be(70);
    }

    [Fact]
    public void Passes_through_when_bucket_matches_native_spacing()
    {
        var bars = OneMinuteBars(5, T0);
        var result = BarTimeframeAggregator.Aggregate(bars, TimeSpan.FromMinutes(1));
        result.Should().BeSameAs(bars);
    }

    [Fact]
    public void Daily_bucket_floors_to_utc_date()
    {
        var bars = new List<Bar>
        {
            new(new DateTime(2026, 1, 5, 23, 59, 0, DateTimeKind.Utc), 1, 2, 0.5, 1.5, 100),
            new(new DateTime(2026, 1, 6, 0, 1, 0, DateTimeKind.Utc), 1.5, 3, 1, 2.5, 200),
        };

        var result = BarTimeframeAggregator.Aggregate(bars, TimeSpan.FromDays(1));

        result.Should().HaveCount(2);
        result[0].TimestampUtc.Should().Be(new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));
        result[1].TimestampUtc.Should().Be(new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc));
        result[1].Open.Should().Be(1.5);
        result[1].Volume.Should().Be(200);
    }

    [Fact]
    public void Empty_input_yields_empty_output()
    {
        BarTimeframeAggregator.Aggregate(Array.Empty<Bar>(), TimeSpan.FromMinutes(5)).Should().BeEmpty();
    }
}
