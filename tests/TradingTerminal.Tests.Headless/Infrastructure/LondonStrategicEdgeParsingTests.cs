using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.LondonStrategicEdge;
using Xunit;

namespace TradingTerminal.Tests.Infrastructure;

/// <summary>
/// Offline tests for the London Strategic Edge WebSocket/REST JSON parsers and symbol mapping.
/// No network — they feed the pure deterministic helpers real-shaped payloads (per the provider's
/// published lse-data SDK) and assert the mapping into the canonical domain records.
/// </summary>
public sealed class LondonStrategicEdgeParsingTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static Contract C(string symbol, string secType = "STK") =>
        new(symbol, secType, "LSE", "USD", string.Empty);

    // ── ToLseSymbol ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToLseSymbol_passes_through_slash_pairs()
    {
        RealLondonStrategicEdgeClient.ToLseSymbol(C("BTC/USD", "CRYPTO")).Should().Be("BTC/USD");
    }

    [Fact]
    public void ToLseSymbol_passes_through_plain_tickers()
    {
        RealLondonStrategicEdgeClient.ToLseSymbol(C("AAPL")).Should().Be("AAPL");
    }

    [Fact]
    public void ToLseSymbol_splits_bare_six_letter_cash_pairs()
    {
        RealLondonStrategicEdgeClient.ToLseSymbol(C("eurusd", "CASH")).Should().Be("EUR/USD");
    }

    [Fact]
    public void ToLseSymbol_leaves_non_cash_six_letter_symbols_alone()
    {
        RealLondonStrategicEdgeClient.ToLseSymbol(C("GOOGLE")).Should().Be("GOOGLE");
    }

    // ── ParseTick ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseTick_maps_bid_ask_and_iso_timestamp()
    {
        var tick = RealLondonStrategicEdgeClient.ParseTick(Json(
            """{"type":"tick","symbol":"AAPL","price":201.52,"bid":201.51,"ask":201.53,"volume":120,"ts":"2026-06-12T14:30:00Z","name":"Apple Inc","replay":false}"""));

        tick.Should().NotBeNull();
        tick!.Bid.Should().Be(201.51);
        tick.Ask.Should().Be(201.53);
        tick.TimestampUtc.Should().Be(new DateTime(2026, 6, 12, 14, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ParseTick_falls_back_to_price_when_bid_ask_null()
    {
        var tick = RealLondonStrategicEdgeClient.ParseTick(Json(
            """{"type":"tick","symbol":"XAU/USD","price":2410.5,"bid":null,"ask":null,"volume":null,"ts":null,"replay":false}"""));

        tick.Should().NotBeNull();
        tick!.Bid.Should().Be(2410.5);
        tick.Ask.Should().Be(2410.5);
    }

    [Fact]
    public void ParseTick_uses_price_for_missing_side_only()
    {
        var tick = RealLondonStrategicEdgeClient.ParseTick(Json(
            """{"type":"tick","symbol":"AAPL","price":100.0,"bid":99.9}"""));

        tick.Should().NotBeNull();
        tick!.Bid.Should().Be(99.9);
        tick.Ask.Should().Be(100.0);
    }

    [Fact]
    public void ParseTick_returns_null_without_any_price()
    {
        RealLondonStrategicEdgeClient.ParseTick(Json("""{"type":"tick","symbol":"AAPL"}""")).Should().BeNull();
    }

    // ── Candles (PostgREST rows) ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseCandles_maps_rows_and_rounds_volume()
    {
        var bars = RealLondonStrategicEdgeClient.ParseCandles(Json(
            """
            [
              {"timestamp":"2026-06-11T00:00:00Z","open":1.0,"high":2.0,"low":0.5,"close":1.5,"volume":1234.6},
              {"timestamp":"2026-06-12T00:00:00Z","open":1.5,"high":2.5,"low":1.0,"close":2.0,"volume":null}
            ]
            """));

        bars.Should().HaveCount(2);
        bars[0].Open.Should().Be(1.0);
        bars[0].Volume.Should().Be(1235);
        bars[0].TimestampUtc.Should().Be(new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc));
        bars[1].Volume.Should().Be(0);
    }

    [Fact]
    public void ParseCandles_skips_rows_missing_ohlc()
    {
        var bars = RealLondonStrategicEdgeClient.ParseCandles(Json(
            """[{"timestamp":"2026-06-11T00:00:00Z","open":1.0,"close":1.5}]"""));

        bars.Should().BeEmpty();
    }

    [Theory]
    [InlineData(BarSize.OneMinute, "1m")]
    [InlineData(BarSize.ThreeMinutes, "1m")]
    [InlineData(BarSize.FiveMinutes, "5m")]
    [InlineData(BarSize.FifteenMinutes, "15m")]
    [InlineData(BarSize.OneHour, "1h")]
    [InlineData(BarSize.OneDay, "1d")]
    public void MapTimeframe_matches_provider_tables(BarSize size, string expected)
    {
        RealLondonStrategicEdgeClient.MapTimeframe(size).Should().Be(expected);
    }

    [Theory]
    [InlineData("BTC/USD", "btc_usd")]
    [InlineData("BRK.B", "brk_b")]
    [InlineData("US-500", "us_500")]
    public void SymbolSlug_mirrors_provider_sdk(string symbol, string expected)
    {
        RealLondonStrategicEdgeClient.SymbolSlug(symbol).Should().Be(expected);
    }

    // ── Catalog ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseCatalog_maps_symbol_name_category()
    {
        var list = RealLondonStrategicEdgeClient.ParseCatalog(Json(
            """
            [
              {"symbol":"AAPL","name":"Apple Inc","category":"Stocks"},
              {"symbol":"EUR/USD","name":"Euro / US Dollar","category":"Forex"},
              {"symbol":"BTC/USD","name":"Bitcoin","category":"Crypto"},
              {"symbol":""}
            ]
            """));

        list.Should().HaveCount(3);
        list[0].Contract.SecType.Should().Be("STK");
        list[0].Broker.Should().Be(BrokerKind.LondonStrategicEdge);
        list[1].Contract.SecType.Should().Be("CASH");
        list[1].Contract.Currency.Should().Be("USD");
        list[2].Contract.SecType.Should().Be("CRYPTO");
        list[2].Category.Should().Be("Crypto (LSE)");
    }

    [Theory]
    [InlineData("Stocks", "STK")]
    [InlineData("ETFs", "ETF")]
    [InlineData("Forex", "CASH")]
    [InlineData("Crypto", "CRYPTO")]
    [InlineData("Commodities", "CMDTY")]
    [InlineData("Indices", "IND")]
    [InlineData(null, "STK")]
    public void SecTypeOf_maps_catalog_categories(string? category, string expected)
    {
        RealLondonStrategicEdgeClient.SecTypeOf(category).Should().Be(expected);
    }

    [Theory]
    [InlineData("EUR/GBP", "GBP")]
    [InlineData("BTC/USDT", "USDT")]
    [InlineData("AAPL", "USD")]
    public void QuoteCurrencyOf_reads_the_quote_leg(string symbol, string expected)
    {
        RealLondonStrategicEdgeClient.QuoteCurrencyOf(symbol).Should().Be(expected);
    }
}
