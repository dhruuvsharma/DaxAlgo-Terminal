using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Crypto;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Bars of a size a venue does not publish, built from ones it does — the replacement for answering a
/// three-minute request with five-minute bars under a three-minute label, which Coinbase and Kraken did
/// until 2026-09-25 and which no chart could reveal.
/// </summary>
public sealed class BarRollupTests
{
    private static readonly DateTime T0 = new(2026, 9, 24, 18, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Three = TimeSpan.FromMinutes(3);

    private static Bar Minute(int minute, double open, double high, double low, double close, long volume) =>
        new(T0.AddMinutes(minute), open, high, low, close, volume);

    [Fact]
    public void Three_minutes_become_one_bar_with_first_open_extremes_last_close_and_summed_volume()
    {
        var bars = BarRollup.Rollup(
        [
            Minute(0, 10, 12, 9, 11, 5),
            Minute(1, 11, 15, 10, 14, 7),
            Minute(2, 14, 14, 8, 9, 3),
            Minute(3, 9, 10, 9, 10, 1),
        ], Three);

        bars.Should().HaveCount(2);
        bars[0].Should().Be(new Bar(T0, 10, 15, 8, 9, 15));
        bars[1].TimestampUtc.Should().Be(T0.AddMinutes(3));
    }

    [Fact]
    public void Buckets_align_to_the_epoch_as_venues_candles_do()
    {
        // 18:01 falls in the bucket that starts at 18:00 — not in one that starts at the first bar seen.
        var bars = BarRollup.Rollup([Minute(1, 1, 1, 1, 1, 1), Minute(2, 2, 2, 2, 2, 1)], Three);

        bars.Should().ContainSingle().Which.TimestampUtc.Should().Be(T0);
        BarRollup.BucketStart(new DateTime(2026, 9, 24, 18, 59, 59, DateTimeKind.Utc), TimeSpan.FromMinutes(15))
            .Should().Be(new DateTime(2026, 9, 24, 18, 45, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Order_of_the_input_does_not_matter()
    {
        var forward = BarRollup.Rollup([Minute(0, 10, 12, 9, 11, 5), Minute(1, 11, 15, 10, 14, 7)], Three);
        var reversed = BarRollup.Rollup([Minute(1, 11, 15, 10, 14, 7), Minute(0, 10, 12, 9, 11, 5)], Three);

        reversed.Should().Equal(forward, "venues send newest-first as often as oldest-first");
    }

    [Fact]
    public void Normalise_sorts_dedupes_and_keeps_the_newest()
    {
        var bars = BarRollup.Normalise(
        [
            Minute(2, 1, 1, 1, 1, 1),
            Minute(0, 1, 1, 1, 1, 1),
            Minute(1, 1, 1, 1, 1, 1),
            Minute(2, 2, 2, 2, 2, 2),
        ], count: 2);

        bars.Select(b => b.TimestampUtc).Should().Equal(T0.AddMinutes(1), T0.AddMinutes(2));
        bars[^1].Close.Should().Be(2, "a repeated timestamp keeps the later row — the forming bar's latest state");
    }

    [Fact]
    public void Live_rollup_replaces_a_minute_it_has_seen_rather_than_adding_it_again()
    {
        var rollup = new LiveBarRollup(Three);

        rollup.Push(Minute(0, 10, 11, 10, 11, 2));
        rollup.Push(Minute(0, 10, 12, 10, 12, 5));        // the same minute, updated
        var bar = rollup.Push(Minute(1, 12, 13, 12, 13, 1));

        bar.Should().Be(new Bar(T0, 10, 13, 10, 13, 6), "the minute's volume is its latest total, not the sum of updates");
    }

    [Fact]
    public void Live_rollup_starts_afresh_at_a_new_bucket_and_ignores_a_late_old_minute()
    {
        var rollup = new LiveBarRollup(Three);
        rollup.Push(Minute(2, 10, 10, 10, 10, 1));

        var next = rollup.Push(Minute(3, 20, 21, 19, 20, 4));
        next.Should().Be(new Bar(T0.AddMinutes(3), 20, 21, 19, 20, 4));

        rollup.Push(Minute(2, 99, 99, 99, 99, 99)).Should().Be(next, "a late update for a closed bucket must not move the chart backwards");
    }

    [Fact]
    public void Trade_bars_open_at_the_first_print_and_roll_over_on_the_bucket()
    {
        var bars = new LiveTradeBars(TimeSpan.FromMinutes(1));

        bars.Push(new TradeTick(T0.AddSeconds(5), 100, 2, AggressorSide.Buy));
        bars.Push(new TradeTick(T0.AddSeconds(30), 105, 1, AggressorSide.Buy));
        var bar = bars.Push(new TradeTick(T0.AddSeconds(59), 98, 3, AggressorSide.Sell));
        bar.Should().Be(new Bar(T0, 100, 105, 98, 98, 6));

        bars.Push(new TradeTick(T0.AddSeconds(61), 99, 1, AggressorSide.Buy))
            .Should().Be(new Bar(T0.AddMinutes(1), 99, 99, 99, 99, 1));
    }

    // ── timestamp units ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1790274000L)]                // seconds
    [InlineData(1790274000000L)]             // milliseconds
    [InlineData(1790274000000000L)]          // microseconds — Bithumb's book, Bitstamp
    [InlineData(1790274000000000000L)]       // nanoseconds — KuCoin trades, Bitvavo's book
    public void A_timestamp_is_read_in_whatever_unit_it_arrived(long epoch) =>
        CryptoConvert.MsUtc(epoch).Should().Be(DateTimeOffset.FromUnixTimeSeconds(1790274000).UtcDateTime);

    [Fact]
    public void A_missing_timestamp_is_now_not_1970() =>
        CryptoConvert.MsUtc(0).Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
}
