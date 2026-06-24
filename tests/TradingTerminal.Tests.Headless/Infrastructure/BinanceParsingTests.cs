using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Binance;
using Xunit;

namespace TradingTerminal.Tests.Infrastructure;

/// <summary>
/// Offline tests for the Binance public-feed JSON parsers. No network — they feed the parsers
/// real Binance payload shapes and assert the mapping into the canonical domain records.
/// </summary>
public sealed class BinanceParsingTests
{
    private const double Scale = 1000.0;

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void ParseBookTicker_maps_bid_ask_and_scales_sizes()
    {
        var el = Json("""{"u":400900217,"s":"BTCUSDT","b":"25.35","B":"0.5","a":"25.36","A":"1.25"}""");

        var tick = RealBinanceClient.ParseBookTicker(el, Scale);

        tick.Should().NotBeNull();
        tick!.Bid.Should().Be(25.35);
        tick.Ask.Should().Be(25.36);
        tick.BidSize.Should().Be(500);   // 0.5  * 1000
        tick.AskSize.Should().Be(1250);  // 1.25 * 1000
    }

    [Fact]
    public void ParseBookTicker_returns_null_on_unexpected_shape()
    {
        RealBinanceClient.ParseBookTicker(Json("""{"result":null,"id":1}"""), Scale).Should().BeNull();
        RealBinanceClient.ParseBookTicker(Json("\"pong\""), Scale).Should().BeNull();
    }

    [Theory]
    [InlineData(true, AggressorSide.Sell)]   // buyer is the maker → seller initiated
    [InlineData(false, AggressorSide.Buy)]   // buyer is the taker → buyer initiated
    public void ParseTrade_maps_maker_flag_to_aggressor(bool buyerIsMaker, AggressorSide expected)
    {
        var el = Json($$"""{"e":"trade","E":1700000000123,"s":"BTCUSDT","p":"42010.5","q":"3","T":1700000000100,"m":{{(buyerIsMaker ? "true" : "false")}}}""");

        var trade = RealBinanceClient.ParseTrade(el, Scale);

        trade.Should().NotBeNull();
        trade!.Price.Should().Be(42010.5);
        trade.Size.Should().Be(3000);   // scaled by SizeScale, consistent with quotes/depth/bars
        trade.Aggressor.Should().Be(expected);
        trade.TimestampUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000100).UtcDateTime);
    }

    [Fact]
    public void ParseTrade_scales_fractional_btc_qty_to_nonzero_size()
    {
        // Regression: BTC trades are almost always fractional (a 0.012 BTC print is typical). The
        // old code rounded raw qty to a whole number, flooring sub-1-unit trades to 0 — and the
        // footprint/CVD/VPIN math drops zero-size prints, so the cluster never updated for BTC.
        var el = Json("""{"e":"trade","s":"BTCUSDT","p":"42010.5","q":"0.012","T":1700000000100,"m":false}""");

        var trade = RealBinanceClient.ParseTrade(el, Scale);

        trade.Should().NotBeNull();
        trade!.Size.Should().Be(12);   // round(0.012 * 1000) — survives the Size > 0 filter
    }

    [Fact]
    public void ParseDepth_builds_snapshot_preserving_side_order()
    {
        var el = Json("""
            {
              "lastUpdateId": 160,
              "bids": [["100.5","2"],["100.4","1.5"]],
              "asks": [["100.6","0.25"],["100.7","4"]]
            }
            """);

        var depth = RealBinanceClient.ParseDepth(el, Scale);

        depth.Should().NotBeNull();
        depth!.Bids.Should().HaveCount(2);
        depth.Asks.Should().HaveCount(2);
        depth.BestBid.Should().Be(100.5);
        depth.BestBidSize.Should().Be(2000);  // 2 * 1000
        depth.BestAsk.Should().Be(100.6);
        depth.BestAskSize.Should().Be(250);   // 0.25 * 1000
    }

    [Fact]
    public void ParseKline_maps_ohlcv_and_open_time()
    {
        var el = Json("""
            {
              "e":"kline","E":1700000005000,"s":"BTCUSDT",
              "k":{"t":1700000000000,"T":1700000059999,"i":"1m",
                   "o":"100","h":"110","l":"95","c":"105","v":"12.5","x":false}
            }
            """);

        var bar = RealBinanceClient.ParseKline(el, Scale);

        bar.Should().NotBeNull();
        bar!.Open.Should().Be(100);
        bar.High.Should().Be(110);
        bar.Low.Should().Be(95);
        bar.Close.Should().Be(105);
        bar.Volume.Should().Be(12500); // 12.5 * 1000
        bar.TimestampUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime);
    }

    [Fact]
    public void ParseHistoricalKlines_maps_rows_oldest_first()
    {
        var el = Json("""
            [
              [1700000000000,"100","110","95","105","10",1700000059999,"0",5,"0","0","0"],
              [1700000060000,"105","112","104","108","20",1700000119999,"0",7,"0","0","0"]
            ]
            """);

        var bars = RealBinanceClient.ParseHistoricalKlines(el, Scale);

        bars.Should().HaveCount(2);
        bars[0].Open.Should().Be(100);
        bars[0].Volume.Should().Be(10000); // 10 * 1000
        bars[1].Close.Should().Be(108);
        bars[1].TimestampUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000060000).UtcDateTime);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(5, 5)]
    [InlineData(7, 10)]
    [InlineData(10, 10)]
    [InlineData(11, 20)]
    [InlineData(50, 20)]
    public void BinanceDepthLevels_snaps_to_supported_tiers(int requested, int expected)
    {
        RealBinanceClient.BinanceDepthLevels(requested).Should().Be(expected);
    }

    [Theory]
    [InlineData(BarSize.OneMinute, "1m")]
    [InlineData(BarSize.FiveMinutes, "5m")]
    [InlineData(BarSize.OneHour, "1h")]
    [InlineData(BarSize.OneDay, "1d")]
    public void MapInterval_maps_bar_sizes(BarSize size, string expected)
    {
        RealBinanceClient.MapInterval(size).Should().Be(expected);
    }

    // ── aggTrades (historical tape for the full-tape quick backtest) ────────────────────────────

    [Fact]
    public void ParseAggTrades_maps_prints_aggressor_and_cursor()
    {
        // Two aggTrades: first buyer-is-maker (seller aggressor), second buyer-is-taker (buyer aggressor).
        var el = Json("""
            [
              {"a":101,"p":"42010.5","q":"0.012","f":201,"l":201,"T":1700000000100,"m":true},
              {"a":102,"p":"42011.0","q":"3","f":202,"l":205,"T":1700000000200,"m":false}
            ]
            """);

        var (trades, lastId, lastTime) = RealBinanceClient.ParseAggTrades(el, Scale, endMs: long.MaxValue);

        trades.Should().HaveCount(2);
        trades[0].Price.Should().Be(42010.5);
        trades[0].Size.Should().Be(12);                 // round(0.012 * 1000) — survives the Size>0 filter
        trades[0].Aggressor.Should().Be(AggressorSide.Sell);
        trades[1].Aggressor.Should().Be(AggressorSide.Buy);
        trades[1].Size.Should().Be(3000);
        lastId.Should().Be(102);                         // pagination cursor = last aggTrade id
        lastTime.Should().Be(1700000000200);
    }

    [Fact]
    public void ParseAggTrades_drops_prints_past_end_but_still_advances_cursor()
    {
        // The second print is past the window end: it must NOT be returned, but lastId/lastTime must
        // still advance so the caller's fromId pagination terminates instead of looping forever.
        var el = Json("""
            [
              {"a":10,"p":"100","q":"1","T":1700000000100,"m":false},
              {"a":11,"p":"101","q":"1","T":1700000000500,"m":false}
            ]
            """);

        var (trades, lastId, lastTime) = RealBinanceClient.ParseAggTrades(el, Scale, endMs: 1700000000300);

        trades.Should().HaveCount(1);
        trades[0].Price.Should().Be(100);
        lastId.Should().Be(11);
        lastTime.Should().Be(1700000000500);
    }

    [Fact]
    public void ParseAggTrades_signals_empty_response_with_negative_id()
    {
        var (trades, lastId, _) = RealBinanceClient.ParseAggTrades(Json("[]"), Scale, endMs: long.MaxValue);
        trades.Should().BeEmpty();
        lastId.Should().Be(-1);   // the caller uses lastId < 0 to detect "no data"
    }
}
