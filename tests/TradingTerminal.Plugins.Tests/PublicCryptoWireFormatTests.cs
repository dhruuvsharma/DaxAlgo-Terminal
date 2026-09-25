using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Bitfinex;
using TradingTerminal.Infrastructure.Bitget;
using TradingTerminal.Infrastructure.Bitstamp;
using TradingTerminal.Infrastructure.Bitvavo;
using TradingTerminal.Infrastructure.Crypto;
using TradingTerminal.Infrastructure.CryptoCom;
using TradingTerminal.Infrastructure.GateIo;
using TradingTerminal.Infrastructure.Gemini;
using TradingTerminal.Infrastructure.Htx;
using TradingTerminal.Infrastructure.KuCoin;
using TradingTerminal.Infrastructure.Mexc;
using TradingTerminal.Infrastructure.Upbit;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The wire shapes of the twelve venues added on 2026-09-25, pinned to frames captured from the live
/// venues that day (trimmed to a few rows; nothing else changed).
///
/// <para>Each test names the trap that shape sets — a column order nobody else uses, history arriving on
/// a live channel, a timestamp in an unexpected unit. Those are the things documentation gets wrong and a
/// renamed field silently breaks; parsing is pure, so none of this needs an account or a network.</para>
/// </summary>
public sealed class PublicCryptoWireFormatTests
{
    private const double Scale = 1000.0;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // ── Bitget ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bitget_ticker_carries_the_touch()
    {
        var tick = RealBitgetClient.ParseTicker(Json("""
            {"action":"snapshot","arg":{"instType":"SPOT","channel":"ticker","instId":"BTCUSDT"},
             "data":[{"instId":"BTCUSDT","lastPr":"84404.26","bidPr":"84404.25","askPr":"84404.26",
                      "bidSz":"0.824692","askSz":"0.136309","ts":"1790274192026"}],"ts":1790274192029}
            """), Scale).Single();

        tick.Bid.Should().Be(84404.25);
        tick.Ask.Should().Be(84404.26);
        tick.BidSize.Should().Be(825);
        tick.TimestampUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1790274192026).UtcDateTime);
    }

    [Fact]
    public void Bitget_trade_snapshot_is_history_and_is_not_replayed_into_the_tape()
    {
        const string Snapshot = """
            {"action":"snapshot","arg":{"channel":"trade"},"data":[{"ts":"1790274193197","price":"84404.26","size":"0.000065","side":"buy"}]}
            """;
        RealBitgetClient.ParseTrades(Json(Snapshot), Scale).Should().BeEmpty();

        var trades = RealBitgetClient.ParseTrades(Json("""
            {"action":"update","arg":{"channel":"trade"},"data":[
              {"ts":"1790274196603","price":"84404.25","size":"0.004339","side":"sell"},
              {"ts":"1790274196602","price":"84404.20","size":"0.002000","side":"buy"}]}
            """), Scale).ToList();

        trades.Should().HaveCount(2);
        trades[0].TimestampUtc.Should().BeBefore(trades[1].TimestampUtc, "newest-first is reversed so the tape runs forward");
        trades[1].Aggressor.Should().Be(AggressorSide.Sell);
    }

    [Fact]
    public void Bitget_candle_snapshot_keeps_only_the_forming_candle()
    {
        var bars = RealBitgetClient.ParseCandles(Json("""
            {"action":"snapshot","arg":{"channel":"candle1m"},"data":[
              ["1790244240000","83381.83","83381.83","83332.26","83341.41","1.432097","119356.8","119356.8"],
              ["1790244300000","83341.41","83348.5","83301","83305.18","1.394684","116204.7","116204.7"]]}
            """), Scale).ToList();

        bars.Should().ContainSingle().Which.Close.Should().Be(83305.18);
    }

    // ── KuCoin ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void KuCoin_candles_run_open_close_high_low_not_ohlc()
    {
        var bar = RealKuCoinClient.ParseCandle(Json("""
            {"topic":"/market/candles:BTC-USDT_1min","type":"message","subject":"trade.candles.update",
             "data":{"symbol":"BTC-USDT","candles":["1790274180","84410.2","84390.7","84410.2","84390.6","0.63004204","53174.0"],"time":1790274226100954919}}
            """), Scale).Single();

        bar.Open.Should().Be(84410.2);
        bar.Close.Should().Be(84390.7);
        bar.High.Should().Be(84410.2);
        bar.Low.Should().Be(84390.6);
        bar.TimestampUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1790274180).UtcDateTime);
    }

    [Fact]
    public void KuCoin_trade_time_is_nanoseconds_in_a_string()
    {
        var trade = RealKuCoinClient.ParseTrade(Json("""
            {"topic":"/market/match:BTC-USDT","type":"message","data":{"price":"84390.6","side":"sell","size":"0.0003426","time":"1790274222318000000"}}
            """), Scale).Single();

        trade.TimestampUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1790274222318).UtcDateTime);
        trade.Aggressor.Should().Be(AggressorSide.Sell);
    }

    [Fact]
    public void KuCoin_socket_url_carries_the_token_it_was_issued()
    {
        var url = RealKuCoinClient.SocketUrl(
            """{"code":"200000","data":{"token":"t0k+en=","instanceServers":[{"endpoint":"wss://ws-api-spot.kucoin.com/","pingInterval":18000}]}}""",
            "wss://fallback/", "c1");

        url.Should().Be("wss://ws-api-spot.kucoin.com/?token=t0k%2Ben%3D&connectId=c1");
        RealKuCoinClient.SocketUrl("""{"code":"200000","data":{}}""", "wss://fallback/", "c1").Should().BeNull();
    }

    // ── Gate.io ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GateIo_rest_candles_put_the_open_second_to_last()
    {
        var bar = RealGateIoClient.ParseRestCandles(Json("""
            [["1790274120","11662.91109010","84404","84421.4","84384.2","84384.2","0.13818000","true"]]
            """), Scale).Single();

        bar.Open.Should().Be(84384.2);
        bar.High.Should().Be(84421.4);
        bar.Close.Should().Be(84404);
        bar.Volume.Should().Be(138, "the base amount, not the quote turnover in column 1");
    }

    [Fact]
    public void GateIo_trade_time_has_a_fractional_millisecond()
    {
        var trade = RealGateIoClient.ParseTrade(Json("""
            {"channel":"spot.trades","event":"update","result":{"create_time_ms":"1790274249452.965000","side":"buy","amount":"0.0039","price":"84377.7"}}
            """), Scale).Single();

        trade.TimestampUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1790274249452).UtcDateTime);
        trade.Price.Should().Be(84377.7);
    }

    // ── Gemini ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gemini_l1_is_the_top_of_the_book_it_maintains()
    {
        var book = new L2OrderBook();
        RealGeminiClient.ParseTop(Json("""
            {"type":"l2_updates","symbol":"BTCUSD","changes":[["buy","84370.24","0.1"],["buy","84364.5","0.2"],["sell","84376.89","0.3"]],"trades":[]}
            """), book, Scale).Single().Bid.Should().Be(84370.24);

        var tick = RealGeminiClient.ParseTop(Json("""
            {"type":"l2_updates","symbol":"BTCUSD","changes":[["buy","84370.24","0.0"]]}
            """), book, Scale).Single();

        tick.Bid.Should().Be(84364.5, "a quantity of zero removes the level");
        tick.Ask.Should().Be(84376.89);
    }

    [Fact]
    public void Gemini_candles_open_with_a_day_of_history()
    {
        var first = RealGeminiClient.ParseCandles(Json("""
            {"type":"candles_1m_updates","symbol":"BTCUSD","changes":[[1790274180000,84383.7,84383.7,84357.7,84368.81,0.129],[1790274120000,84359.81,84397.8,84359.81,84383.7,0.004]]}
            """), Scale, onlyNewest: true);

        first.Should().ContainSingle().Which.TimestampUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1790274180000).UtcDateTime);
    }

    // ── Crypto.com ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CryptoCom_heartbeat_is_answered_with_its_own_id()
    {
        RealCryptoComClient.HeartbeatReply(Json("""{"id":1790274309177,"method":"public/heartbeat","code":0}"""))
            .Should().Be("""{"id":1790274309177,"method":"public/respond-heartbeat"}""");
        RealCryptoComClient.HeartbeatReply(Json("""{"id":-1,"method":"subscribe","result":{}}""")).Should().BeNull();
    }

    [Fact]
    public void CryptoCom_subscribe_response_is_history_and_live_pushes_are_id_minus_one()
    {
        const string Response = """
            {"id":1,"method":"subscribe","code":0,"result":{"channel":"trade","data":[{"t":1790274295599,"p":"84376.36","q":"0.01099","s":"BUY"}]}}
            """;
        RealCryptoComClient.ParseTrades(Json(Response), Scale).Should().BeEmpty();

        var trade = RealCryptoComClient.ParseTrades(Json("""
            {"id":-1,"method":"subscribe","code":0,"result":{"channel":"trade","data":[{"t":1790274304153,"p":"84376.36","q":"0.01627","s":"SELL"}]}}
            """), Scale).Single();
        trade.Aggressor.Should().Be(AggressorSide.Sell);
    }

    [Fact]
    public void CryptoCom_ticker_names_its_touch_b_and_k()
    {
        var tick = RealCryptoComClient.ParseTicker(Json("""
            {"id":-1,"method":"subscribe","code":0,"result":{"channel":"ticker","data":[{"a":"84366.49","b":"84366.48","bs":"0.43959","k":"84366.49","ks":"0.43748","t":1790274291799}]}}
            """), Scale).Single();

        tick.Bid.Should().Be(84366.48);
        tick.Ask.Should().Be(84366.49);
        tick.AskSize.Should().Be(437);
    }

    // ── Upbit / Bithumb ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Upbit_orderbook_units_become_both_sides_of_the_book()
    {
        var depth = UpbitShapedClient<UpbitOptions>.ParseBook(Json("""
            {"type":"orderbook","code":"KRW-BTC","timestamp":1790274371166,"orderbook_units":[
              {"ask_price":1.15086E8,"bid_price":1.15068E8,"ask_size":0.00060539,"bid_size":0.00018},
              {"ask_price":1.15104E8,"bid_price":1.15035E8,"ask_size":0.00034855,"bid_size":0.02211077}]}
            """), 10, Scale).Single();

        depth.BestBid.Should().Be(115068000);
        depth.BestAsk.Should().Be(115086000);
        depth.Bids.Should().HaveCount(2);
    }

    [Fact]
    public void Bithumb_stamps_its_book_in_microseconds_and_that_is_read_not_thrown()
    {
        // Read as milliseconds this is the year 58,700 — an exception, which dropped every Bithumb book
        // frame until the unit was read from the magnitude.
        var depth = UpbitShapedClient<BithumbOptions>.ParseBook(Json("""
            {"type":"orderbook","code":"KRW-BTC","orderbook_units":[{"ask_price":115170000,"bid_price":114984000,"ask_size":0.0535,"bid_size":0.0055}],"level":1,"timestamp":1790275855119979,"stream_type":"SNAPSHOT"}
            """), 10, Scale).Single();

        depth.TimestampUtc.Should().Be(DateTime.UnixEpoch.AddTicks(1790275855119979L * 10));
    }

    [Fact]
    public void Upbit_trade_snapshot_is_skipped_and_ask_means_a_seller()
    {
        RealUpbitClient.ParseTrade(Json("""
            {"type":"trade","code":"KRW-BTC","trade_price":114964000,"trade_volume":0.02,"ask_bid":"ASK","trade_timestamp":1790274354515,"stream_type":"SNAPSHOT"}
            """), Scale).Should().BeEmpty();

        RealUpbitClient.ParseTrade(Json("""
            {"type":"trade","code":"KRW-BTC","trade_price":114964000,"trade_volume":0.02,"ask_bid":"ASK","trade_timestamp":1790274354515,"stream_type":"REALTIME"}
            """), Scale).Single().Aggressor.Should().Be(AggressorSide.Sell);
    }

    [Fact]
    public void Upbit_candle_time_is_the_utc_field_not_the_korea_one()
    {
        var bar = RealUpbitClient.ParseCandle(Json("""
            {"type":"candle.1m","code":"KRW-BTC","candle_date_time_utc":"2026-09-24T18:26:00","candle_date_time_kst":"2026-09-25T03:26:00",
             "opening_price":115051000,"high_price":115086000,"low_price":115051000,"trade_price":115086000,"candle_acc_trade_volume":0.0022808,"stream_type":"SNAPSHOT"}
            """), Scale).Single();

        bar.TimestampUtc.Should().Be(new DateTime(2026, 9, 24, 18, 26, 0, DateTimeKind.Utc));
        bar.TimestampUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Theory]
    [InlineData(0, "2026-09-24T17:00:00Z", "2026-09-24T00:00:00Z")]      // Upbit: the UTC day
    [InlineData(-9, "2026-09-24T17:00:00Z", "2026-09-24T15:00:00Z")]     // Bithumb: Korea's day began 15:00 UTC
    [InlineData(-9, "2026-09-24T10:00:00Z", "2026-09-23T15:00:00Z")]
    public void The_daily_bar_starts_when_the_venues_day_does(int offsetHours, string now, string expected)
    {
        var ms = DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        var bar = RealUpbitClient.ParseDailyTicker(Json($$"""
            {"type":"ticker","code":"KRW-BTC","opening_price":1,"high_price":3,"low_price":1,"trade_price":2,"acc_trade_volume":5,"timestamp":{{ms}}}
            """), TimeSpan.FromHours(offsetHours), Scale).Single();

        bar.TimestampUtc.Should().Be(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime);
    }

    // ── Bitfinex ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bitfinex_book_count_zero_removes_whatever_the_amount_says()
    {
        var book = new L2OrderBook();
        RealBitfinexClient.ParseBook(Json("[43824,[[84315,1,0.00022],[84312,1,0.5],[84330,2,-0.3]]]"), book, 10, Scale).Single()
            .BestBid.Should().Be(84315);

        var after = RealBitfinexClient.ParseBook(Json("[43824,[84315,0,1]]"), book, 10, Scale).Single();

        after.BestBid.Should().Be(84312);
        after.BestAsk.Should().Be(84330, "a negative amount is the ask side");
    }

    [Fact]
    public void Bitfinex_reads_only_executed_trades()
    {
        RealBitfinexClient.ParseTrade(Json("[917,\"hb\"]"), Scale).Should().BeEmpty();
        RealBitfinexClient.ParseTrade(Json("[917,\"tu\",[1981875393,1790274359840,-0.00022,84315]]"), Scale).Should().BeEmpty();

        var trade = RealBitfinexClient.ParseTrade(Json("[917,\"te\",[1981875393,1790274359840,-0.00022,84315]]"), Scale).Single();
        trade.Aggressor.Should().Be(AggressorSide.Sell);
        trade.Price.Should().Be(84315);
    }

    [Fact]
    public void Bitfinex_candles_run_open_close_high_low()
    {
        var bar = RealBitfinexClient.ParseCandles(Json("[43412,[1790274300000,84326,84312,84409,84312,2.86170018]]"), Scale).Single();

        bar.Open.Should().Be(84326);
        bar.Close.Should().Be(84312);
        bar.High.Should().Be(84409);
    }

    // ── Bitstamp ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bitstamp_trade_type_one_is_a_sell_and_time_is_microseconds()
    {
        var trade = RealBitstampClient.ParseTrade(Json("""
            {"data":{"id":642641703,"amount":0.0780372,"price":84416.45,"type":1,"microtimestamp":"1790274590598000"},"channel":"live_trades_btcusd","event":"trade"}
            """), Scale).Single();

        trade.Aggressor.Should().Be(AggressorSide.Sell);
        trade.TimestampUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1790274590598).UtcDateTime);
    }

    // ── Bitvavo ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bitvavo_ticker_remembers_the_side_a_push_left_out()
    {
        var state = new RealBitvavoClient.TickerState();
        RealBitvavoClient.ParseTicker(Json("""{"event":"ticker","market":"BTC-EUR","bestBid":"74238","bestBidSize":"0.1"}"""), state, Scale)
            .Should().BeEmpty("the ask is not known yet");

        var tick = RealBitvavoClient.ParseTicker(Json("""{"event":"ticker","market":"BTC-EUR","bestAsk":"74239","bestAskSize":"0.00794331"}"""), state, Scale).Single();

        tick.Bid.Should().Be(74238, "the bid from the earlier push is kept");
        tick.Ask.Should().Be(74239);
    }

    [Fact]
    public void Bitvavo_book_skips_what_the_snapshot_holds_and_resyncs_on_a_gap()
    {
        var book = new RealBitvavoClient.NonceBook();
        book.Seed(Json("""{"market":"BTC-EUR","nonce":10,"bids":[["74264","0.07"]],"asks":[["74265","0.18"]]}"""));

        RealBitvavoClient.ParseBookUpdate(Json("""{"event":"book","nonce":10,"bids":[["74264","0"]],"asks":[]}"""), book, 10, Scale)
            .Should().BeEmpty("nonce 10 is already in the snapshot");

        RealBitvavoClient.ParseBookUpdate(Json("""{"event":"book","nonce":11,"bids":[["74263","0.5"]],"asks":[],"timestamp":1790274430336936180}"""), book, 10, Scale)
            .Single().Bids.Should().HaveCount(2);

        var gap = () => RealBitvavoClient.ParseBookUpdate(Json("""{"event":"book","nonce":13,"bids":[],"asks":[]}"""), book, 10, Scale).ToList();
        gap.Should().Throw<CryptoStreamResyncException>();
    }

    // ── HTX ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Htx_ping_is_answered_with_the_same_number()
    {
        RealHtxClient.PongFor(Json("""{"ping":1790274494310}""")).Should().Be("""{"pong":1790274494310}""");
        RealHtxClient.PongFor(Json("""{"ch":"market.btcusdt.bbo","tick":{}}""")).Should().BeNull();
    }

    [Fact]
    public void Htx_frames_are_gzipped()
    {
        var text = """{"ch":"market.btcusdt.bbo","ts":1790274492193,"tick":{"ask":84461.79,"askSize":0.002828,"bid":84446.04,"bidSize":4.249964,"quoteTime":1790274492201}}"""u8.ToArray();
        using var packed = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(text);

        var tick = RealHtxClient.ParseBbo(Json(System.Text.Encoding.UTF8.GetString(RealHtxClient.Gunzip(packed.ToArray()))), Scale).Single();
        tick.Bid.Should().Be(84446.04);
        tick.AskSize.Should().Be(3);
    }

    [Fact]
    public void Htx_kline_amount_is_the_base_volume()
    {
        var bar = RealHtxClient.ParseKline(Json("""
            {"ch":"market.btcusdt.kline.1min","ts":1790274492203,"tick":{"id":1790274480,"open":84446.05,"close":84446.05,"low":84446.05,"high":84446.05,"amount":0.018121,"vol":1530.24687205,"count":8}}
            """), Scale).Single();

        bar.Volume.Should().Be(18);
    }

    // ── MEXC (protocol buffers, frames captured 2026-09-25) ─────────────────────────────────────

    private const string MexcBookTicker =
        "0A3473706F74407075626C69632E61676772652E626F6F6B5469636B65722E76332E6170692E7062403130306D7340425443555344541A074254435553445430BAB6CFA88D34DA133B0A0538343237301208302E3030363338371A0838343237302E3031220A352E34323136393936392A0B383231363431353334333630979FCFA88D34";

    private const string MexcDeals =
        "0A2F73706F74407075626C69632E61676772652E6465616C732E76332E6170692E7062403130306D7340425443555344541A074254435553445430AACCCFA88D34D213BB010A470A053834323730120A302E3030303939383239180220E0CBCFA88D342A2937333139303138393639383533393932393658305F37333139303138393639383533393932393758300A470A053834323730120A302E3030303139313639180120E1CBCFA88D342A2937333139303138393639383533393932393858305F3733313930313839363938353339393239395830122773706F74407075626C69632E61676772652E6465616C732E76332E6170692E7062403130306D73";

    private const string MexcDepth =
        "0A2B73706F74407075626C69632E6C696D69742E64657074682E76332E6170692E7062404254435553445440351A07425443555344543093D4CFA88D34FA12A7020A160A0838343237302E3031120A352E34323136393936390A160A0838343237302E3032120A302E30303131373634360A160A0838343237302E3033120A302E30303132343339370A160A0838343237302E3035120A302E30303132323238340A160A0838343237302E3236120A302E303031303839343712160A0838343237302E3030120A302E303036333837303012160A0838343236372E3033120A302E303032333438303012160A0838343236342E3135120A302E303437383935303612160A0838343236332E3435120A302E303437383935303612160A0838343236332E3430120A322E30373835333130301A2173706F74407075626C69632E6C696D69742E64657074682E76332E6170692E7062220B38323136343135353634332887D4CFA88D34";

    private const string MexcKline =
        "0A2873706F74407075626C69632E6B6C696E652E76332E6170692E70624042544355534454404D696E311A07425443555344542220326662393432313534656634346134616232656639386338616662366134613728F0E1CFA88D34A2134B0A044D696E3110F08BD6D5061A0838343330302E3134220538343237302A0838343330302E3135320538343237303A0A322E383430343432303342093233393339332E363948AC8CD6D506";

    [Fact]
    public void Mexc_book_ticker_decodes_from_protobuf()
    {
        var tick = RealMexcClient.ParseBookTicker(Convert.FromHexString(MexcBookTicker), Scale).Single();

        tick.Bid.Should().Be(84270);
        tick.Ask.Should().Be(84270.01);
        tick.AskSize.Should().Be(5422);
    }

    [Fact]
    public void Mexc_deal_type_two_is_a_sell()
    {
        var deals = RealMexcClient.ParseDeals(Convert.FromHexString(MexcDeals), Scale);

        deals.Should().HaveCount(2);
        deals.Select(d => d.Aggressor).Should().Contain([AggressorSide.Sell, AggressorSide.Buy]);
        deals.Should().OnlyContain(d => d.Price == 84270);
    }

    [Fact]
    public void Mexc_depth_field_one_is_asks_and_two_is_bids()
    {
        var depth = RealMexcClient.ParseDepth(Convert.FromHexString(MexcDepth), 10, Scale).Single();

        depth.BestAsk.Should().Be(84270.01);
        depth.BestBid.Should().Be(84270.00);
        depth.Asks.Should().HaveCount(5);
        depth.Bids.Should().HaveCount(5);
    }

    [Fact]
    public void Mexc_kline_fields_run_open_close_high_low()
    {
        var bar = RealMexcClient.ParseKline(Convert.FromHexString(MexcKline), Scale).Single();

        bar.Open.Should().Be(84300.14);
        bar.Close.Should().Be(84270);
        bar.High.Should().Be(84300.15);
        bar.Low.Should().Be(84270);
        bar.TimestampUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1790281200).UtcDateTime, "window start, a varint in seconds");
    }

    [Fact]
    public void Mexc_ignores_a_frame_for_a_different_channel()
    {
        // A kline frame handed to the book-ticker reader yields nothing rather than garbage.
        RealMexcClient.ParseBookTicker(Convert.FromHexString(MexcKline), Scale).Should().BeEmpty();
    }
}
