using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.AliceBlue;
using TradingTerminal.Infrastructure.AngelOne;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Dhan;
using TradingTerminal.Infrastructure.FivePaisa;
using TradingTerminal.Infrastructure.Fyers;
using TradingTerminal.Infrastructure.IciciBreeze;
using TradingTerminal.Infrastructure.Zerodha;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The Indian brokers' wire formats, pinned to what each broker publishes.
///
/// <para><b>Where the fixtures come from.</b> None of these brokers serves data without an account, so no
/// frame could be captured live. Binary packets are <i>built</i> here from each broker's published byte
/// layout (Kite's documented table and its Python client's byte order; SmartAPI's and DhanHQ's Python
/// clients' <c>struct</c> formats). JSON samples are the documentation's own where it gives one (Kite,
/// DhanHQ, 5paisa's feed), and otherwise the shape this adapter assumes — marked as an assumption, so the
/// first real response that disagrees fails here rather than on a chart. Checksums are checked against
/// openssl.</para>
/// </summary>
public sealed class IndiaBrokerWireFormatTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // ── Shared ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://127.0.0.1/?action=login&type=login&status=success&request_token=abc123", "abc123")]
    [InlineData("request_token=abc123&status=success", "abc123")]
    [InlineData("  abc123  ", "abc123")]
    [InlineData("https://127.0.0.1/?status=error", "")]
    public void A_sign_in_proof_is_read_from_a_url_a_query_or_the_bare_value(string pasted, string expected) =>
        SignInProof.Parameter(pasted, "request_token").Should().Be(expected);

    [Theory]
    [InlineData("2017-12-15T09:15:00+0530", "2017-12-15T03:45:00Z")]      // Kite
    [InlineData("2023-09-06T11:15:00+05:30", "2023-09-06T05:45:00Z")]     // Angel One
    [InlineData("2021-05-31T09:15:00", "2021-05-31T03:45:00Z")]           // 5paisa, no offset → IST
    [InlineData("2022-08-15 09:15:00", "2022-08-15T03:45:00Z")]           // Breeze, Alice Blue
    [InlineData("05-Aug-2022 15:29:59", "2022-08-05T09:59:59Z")]          // Breeze ltt
    public void An_indian_timestamp_becomes_utc(string text, string expected) =>
        IndiaTime.ToUtc(text).Should().Be(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime);

    [Theory]
    // RFC 6238, Appendix B, SHA-1, secret "12345678901234567890" — the last six of each eight-digit value.
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Totp_matches_the_rfc_6238_vectors(long unixSeconds, string expected)
    {
        var base32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";   // base32 of the ASCII secret
        Totp.Compute(base32, DateTimeOffset.FromUnixTimeSeconds(unixSeconds)).Should().Be(expected);
        Totp.Compute(base32, DateTimeOffset.FromUnixTimeSeconds(unixSeconds), digits: 8).Should().EndWith(expected);
    }

    [Fact]
    public void A_setup_key_is_accepted_as_authenticator_apps_display_it() =>
        Totp.Compute("gezd gnbv gy3t qojq gezd gnbv gy3t qojq", DateTimeOffset.FromUnixTimeSeconds(59)).Should().Be("287082");

    // ── Zerodha ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Kite_checksum_is_sha256_of_key_token_and_secret() =>
        ZerodhaSignIn.Checksum("kite-key", "req-token", "kite-secret")
            .Should().Be("0b9ad68491d55ceb90d9ccc75a1607dd4cf983f0f50db3d3142c13197930f62b");

    [Fact]
    public void Kite_candles_parse_from_the_documented_sample()
    {
        var bars = RealZerodhaClient.ParseCandles(Json("""
            {"status":"success","data":{"candles":[
              ["2017-12-15T09:15:00+0530", 1704.5, 1705, 1699.25, 1702.8, 2499],
              ["2017-12-15T09:16:00+0530", 1702, 1702, 1698.15, 1698.15, 1271]]}}
            """), 1.0);

        bars.Should().HaveCount(2);
        bars[0].Should().Be(new Bar(new DateTime(2017, 12, 15, 3, 45, 0, DateTimeKind.Utc), 1704.5, 1705, 1699.25, 1702.8, 2499));
    }

    [Fact]
    public void Kite_instrument_token_is_read_from_the_quote()
    {
        RealZerodhaClient.ParseToken(Json("""{"status":"success","data":{"NSE:INFY":{"instrument_token":408065,"last_price":1412.95}}}"""), "NSE:INFY")
            .Should().Be(408065u);
    }

    private static byte[] KiteFullPacket(uint token, int ltpPaise, (int Qty, int Paise)[] bids, (int Qty, int Paise)[] asks, int exchangeTime)
    {
        var p = new byte[184];
        void W(int at, int v) => BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(at, 4), v);
        W(0, (int)token);
        W(4, ltpPaise);
        W(8, 5);             // last quantity
        W(16, 7_360_198);    // volume
        W(60, exchangeTime);
        for (var i = 0; i < 10; i++)
        {
            var (qty, paise) = i < 5 ? (i < bids.Length ? bids[i] : (0, 0)) : (i - 5 < asks.Length ? asks[i - 5] : (0, 0));
            W(64 + i * 12, qty);
            W(64 + i * 12 + 4, paise);
        }

        return p;
    }

    private static byte[] KiteFrame(params byte[][] packets)
    {
        var frame = new List<byte>();
        var count = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(count, (ushort)packets.Length);
        frame.AddRange(count);
        foreach (var packet in packets)
        {
            var length = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)packet.Length);
            frame.AddRange(length);
            frame.AddRange(packet);
        }

        return [.. frame];
    }

    [Fact]
    public void Kite_full_packet_decodes_big_endian_prices_in_paise_and_the_book()
    {
        var frame = KiteFrame(KiteFullPacket(408065, 141295,
            bids: [(100, 141290), (50, 141285)], asks: [(5191, 141295)], exchangeTime: 1623145556));

        var packet = RealZerodhaClient.DecodeFrame(frame).Single();

        packet.Token.Should().Be(408065u);
        packet.LastPrice.Should().Be(1412.95);
        packet.Bids.Should().Equal(new DepthLevel(1412.90, 100), new DepthLevel(1412.85, 50));
        packet.Asks.Should().Equal(new DepthLevel(1412.95, 5191));
        packet.TimeUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1623145556).UtcDateTime);
        packet.ToTick(1).Should().Be(new Tick(packet.TimeUtc, 1412.90, 1412.95, 100, 5191));
    }

    [Fact]
    public void Kite_currency_prices_carry_four_more_decimals()
    {
        // Segment is the token's low byte; 3 is CDS.
        var token = (1234u << 8) | 3u;
        var packet = RealZerodhaClient.DecodeFrame(KiteFrame(KiteFullPacket(token, 832_512_500, [(1, 832_500_000)], [(1, 832_525_000)], 0))).Single();

        packet.LastPrice.Should().BeApproximately(83.25125, 1e-9);
    }

    [Fact]
    public void Kite_index_packet_has_no_book_and_quotes_the_level_itself()
    {
        var p = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(0, 4), 256265);          // NIFTY 50
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(4, 4), 2_512_345);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(28, 4), 1623145556);

        var packet = RealZerodhaClient.DecodeFrame(KiteFrame(p)).Single();

        packet.Bids.Should().BeEmpty();
        packet.ToTick(1).Bid.Should().Be(25123.45);
    }

    [Fact]
    public void Kite_heartbeat_and_text_frames_decode_to_nothing()
    {
        RealZerodhaClient.DecodeFrame([0]).Should().BeEmpty();
        RealZerodhaClient.DecodeFrame(Encoding.UTF8.GetBytes("""{"type":"error","data":"x"}""")).Should().BeEmpty();
    }

    // ── Angel One ──────────────────────────────────────────────────────────────────────────────

    private static byte[] AngelSnapQuote(byte exchangeType, string token, long ltpPaise, long exchangeMs,
        (bool Bid, long Qty, long Paise)[] book)
    {
        var p = new byte[379];
        p[0] = 3;   // snap-quote mode
        p[1] = exchangeType;
        Encoding.ASCII.GetBytes(token).CopyTo(p, 2);
        BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(35, 8), exchangeMs);
        BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(43, 8), ltpPaise);
        for (var i = 0; i < book.Length; i++)
        {
            var at = 147 + i * 20;
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(at, 2), (ushort)(book[i].Bid ? 0 : 1));
            BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(at + 2, 8), book[i].Qty);
            BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(at + 10, 8), book[i].Paise);
        }

        return p;
    }

    [Fact]
    public void Angel_snap_quote_decodes_little_endian_with_a_side_flag_per_level()
    {
        var packet = RealAngelOneClient.DecodePacket(AngelSnapQuote(1, "2885", 294_510, 1_695_992_700_000,
        [
            (true, 120, 294_505), (true, 40, 294_500), (false, 75, 294_515), (false, 10, 294_520),
        ]))!;

        packet.Key.Should().Be("1:2885");
        packet.LastPrice.Should().Be(2945.10);
        packet.Bids.Should().Equal(new DepthLevel(2945.05, 120), new DepthLevel(2945.00, 40));
        packet.Asks.Should().Equal(new DepthLevel(2945.15, 75), new DepthLevel(2945.20, 10));
        packet.TimeUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_695_992_700_000).UtcDateTime);
    }

    [Fact]
    public void Angel_pong_is_not_a_packet() =>
        RealAngelOneClient.DecodePacket(Encoding.ASCII.GetBytes("pong")).Should().BeNull();

    [Fact]
    public void Angel_candles_parse_with_their_ist_offset()
    {
        var bars = RealAngelOneClient.ParseCandles(Json("""
            {"status":true,"message":"SUCCESS","errorcode":"","data":[["2023-09-06T11:15:00+05:30",19571.2,19573.35,19534.4,19552.05,0]]}
            """), 1.0);

        bars.Single().TimestampUtc.Should().Be(new DateTime(2023, 9, 6, 5, 45, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Angel_session_keeps_both_tokens()
    {
        var stored = new AngelSession("jwt-value", "feed-value").Write();
        AngelSession.Read(stored).Should().Be(new AngelSession("jwt-value", "feed-value"));
    }

    [Theory]
    [InlineData("NSE", 1)]
    [InlineData("NFO", 2)]
    [InlineData("BSE", 3)]
    [InlineData("MCX", 5)]
    [InlineData("CDS", 13)]
    public void Angel_exchange_types_are_smartstreams_numbers(string exchange, int expected) =>
        RealAngelOneClient.ExchangeType(exchange).Should().Be(expected);

    // ── Dhan ───────────────────────────────────────────────────────────────────────────────────

    private static byte[] DhanFull(byte segment, int securityId, float ltp, int ltt, (int BidQty, int AskQty, float Bid, float Ask)[] book)
    {
        var p = new byte[162];
        p[0] = 8;
        BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(1, 2), 162);
        p[3] = segment;
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4, 4), securityId);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(8, 4), ltp);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(14, 4), ltt);
        for (var i = 0; i < book.Length; i++)
        {
            var at = 62 + i * 20;
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(at, 4), book[i].BidQty);
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(at + 4, 4), book[i].AskQty);
            BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(at + 12, 4), book[i].Bid);
            BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(at + 16, 4), book[i].Ask);
        }

        return p;
    }

    [Fact]
    public void Dhan_full_packet_decodes_with_both_sides_on_each_level()
    {
        var frame = DhanFull(1, 1333, 1650.25f, 1_695_992_700, [(300, 150, 1650.20f, 1650.30f), (80, 90, 1650.15f, 1650.35f)]);

        var packet = RealDhanClient.DecodeFrame(frame).Single();

        packet.Key.Should().Be("NSE_EQ:1333");
        packet.LastPrice.Should().BeApproximately(1650.25, 1e-3);
        packet.Bids.Select(b => b.Size).Should().Equal(300, 80);
        packet.Asks[0].Price.Should().BeApproximately(1650.30, 1e-3);
    }

    [Fact]
    public void Dhan_frames_can_hold_several_packets_back_to_back()
    {
        var ticker = new byte[16];
        ticker[0] = 2;
        ticker[3] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(ticker.AsSpan(4, 4), 2885);
        BinaryPrimitives.WriteSingleLittleEndian(ticker.AsSpan(8, 4), 2945.1f);

        var packets = RealDhanClient.DecodeFrame([.. ticker, .. DhanFull(1, 1333, 1650f, 0, [])]).ToList();

        packets.Select(p => p.Key).Should().Equal("NSE_EQ:2885", "NSE_EQ:1333");
    }

    [Fact]
    public void Dhan_candles_are_parallel_arrays_in_unix_seconds()
    {
        var bars = RealDhanClient.ParseCandles(Json("""
            {"open":[1650.0,1651.0],"high":[1652.0,1653.5],"low":[1649.0,1650.5],"close":[1651.0,1653.0],"volume":[1200,800],"timestamp":[1695992700,1695992760],"open_interest":[0,0]}
            """), 1.0);

        bars.Should().HaveCount(2);
        bars[1].Should().Be(new Bar(DateTimeOffset.FromUnixTimeSeconds(1695992760).UtcDateTime, 1651.0, 1653.5, 1650.5, 1653.0, 800));
    }

    // ── Fyers (shapes assumed — see the client) ────────────────────────────────────────────────

    [Fact]
    public void Fyers_app_id_hash_is_sha256_of_id_colon_secret() =>
        FyersSignIn.AppIdHash("ABCD-100", "fyers-secret").Should().Be("bd08bd37e742e68206034517db20c5748b4bc47ac8421490bd83c6120bcbc4f7");

    [Fact]
    public void Fyers_depth_names_its_offers_ask_singular()
    {
        var depth = RealFyersClient.ParseDepth(Json("""
            {"s":"ok","d":{"NSE:SBIN-EQ":{"totalbuyqty":1000,"totalsellqty":900,
              "bids":[{"price":560.1,"volume":120,"ord":3},{"price":560.05,"volume":40,"ord":1}],
              "ask":[{"price":560.15,"volume":75,"ord":2}],"ltp":560.1,"ltt":1695992700}}}
            """), "NSE:SBIN-EQ", 1.0)!;

        depth.BestBid.Should().Be(560.1);
        depth.BestAsk.Should().Be(560.15);
        depth.TimestampUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1695992700).UtcDateTime);
    }

    [Fact]
    public void Fyers_candles_are_epoch_rows() =>
        RealFyersClient.ParseCandles(Json("""{"s":"ok","candles":[[1695992700,560.0,561.0,559.5,560.5,15000]]}"""), 1.0)
            .Single().TimestampUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1695992700).UtcDateTime);

    // ── 5paisa ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FivePaisa_depth_message_from_the_documentation_decodes_by_side_flag()
    {
        var packet = RealFivePaisaClient.Decode(Encoding.UTF8.GetBytes("""
            {"Exch":"N","ExchType":"D","Token":71319,"TBidQ":86225,"TOffQ":135100,"Details":[
              {"Quantity":50,"Price":34925.65,"NumberOfOrders":2,"BbBuySellFlag":66},
              {"Quantity":75,"Price":34930.00,"NumberOfOrders":1,"BbBuySellFlag":83}],"Time":"/Date(1640148238196)/"}
            """)).Single();

        packet.Key.Should().Be("D|N:D:71319");
        packet.Depth!.BestBid.Should().Be(34925.65);
        packet.Depth.BestAsk.Should().Be(34930.00);
        packet.Depth.TimestampUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1640148238196).UtcDateTime);
    }

    [Fact]
    public void FivePaisa_feed_messages_arrive_as_an_array()
    {
        var packets = RealFivePaisaClient.Decode(Encoding.UTF8.GetBytes("""
            [{"Exch":"N","ExchType":"C","Token":2885,"LastRate":2945.1,"LastQty":5,"BidRate":2945.05,"BidQty":120,"OffRate":2945.15,"OffQty":75,"TickDt":"/Date(1695992700000)/"}]
            """)).ToList();

        packets.Single().Key.Should().Be("F|N:C:2885");
        packets.Single().Tick.Should().Be(new Tick(DateTimeOffset.FromUnixTimeMilliseconds(1695992700000).UtcDateTime, 2945.05, 2945.15, 120, 75));
    }

    [Fact]
    public void FivePaisa_candles_are_ist_without_an_offset() =>
        RealFivePaisaClient.ParseCandles(Json("""{"status":"success","data":{"candles":[["2021-05-31T09:15:00",1.0,2.0,0.5,1.5,100]]}}"""), 1.0)
            .Single().TimestampUtc.Should().Be(new DateTime(2021, 5, 31, 3, 45, 0, DateTimeKind.Utc));

    // ── Alice Blue ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AliceBlue_session_hashes_match_the_sdk()
    {
        AliceBlueSignIn.UserData("AB123", "alice-key", "enc-key").Should().Be("8bbad9926f2c1347de016b77941ab44d41744ab6824354e7fe2b79c8c9119842");
        RealAliceBlueClient.SusrToken("session-id").Should().Be("3d92ff56d525bfec6f891fd1a0351a4adfb17e9a99084003beebf6145955dca5");
    }

    [Fact]
    public void AliceBlue_feed_updates_merge_into_the_last_known_state()
    {
        var state = new ConcurrentDictionary<string, RealAliceBlueClient.NorenState>(StringComparer.Ordinal);
        RealAliceBlueClient.Merge(state, Encoding.UTF8.GetBytes("""{"t":"tk","e":"NSE","tk":"2885","lp":"2945.10","bp1":"2945.05","sp1":"2945.15","bq1":"120","sq1":"75","ft":"1695992700"}""")).ToList();

        // An update carrying only the new ask leaves the bid as it was.
        var entry = RealAliceBlueClient.Merge(state, Encoding.UTF8.GetBytes("""{"t":"tf","e":"NSE","tk":"2885","sp1":"2945.20"}""")).Single();

        entry.ToTick()!.Bid.Should().Be(2945.05);
        entry.ToTick()!.Ask.Should().Be(2945.20);
        RealAliceBlueClient.Merge(state, Encoding.UTF8.GetBytes("""{"t":"ck","s":"OK"}""")).Should().BeEmpty();
    }

    [Fact]
    public void AliceBlue_depth_is_built_from_numbered_fields()
    {
        var state = new ConcurrentDictionary<string, RealAliceBlueClient.NorenState>(StringComparer.Ordinal);
        var entry = RealAliceBlueClient.Merge(state, Encoding.UTF8.GetBytes(
            """{"t":"dk","e":"NSE","tk":"2885","bp1":"100.5","bq1":"10","bp2":"100.4","bq2":"20","sp1":"100.6","sq1":"5"}""")).Single();

        var depth = entry.ToDepth(5)!;
        depth.Bids.Should().Equal(new DepthLevel(100.5, 10), new DepthLevel(100.4, 20));
        depth.Asks.Should().Equal(new DepthLevel(100.6, 5));
    }

    // ── ICICI Breeze (shapes assumed — see the client) ─────────────────────────────────────────

    [Fact]
    public void Breeze_checksum_is_sha256_of_timestamp_body_and_secret() =>
        RealIciciBreezeClient.Checksum("2026-09-25T01:00:00.000Z", """{"a":1}""", "breeze-secret")
            .Should().Be("ab84a3ce377d4fda6a621b2454516c85f64147d1669cbf0c63aace67eb04c9b9");

    [Fact]
    public void Breeze_quote_is_read_for_the_asked_exchange()
    {
        var tick = RealIciciBreezeClient.ParseQuote(Json("""
            {"Success":[
              {"exchange_code":"BSE","stock_code":"RELIND","ltp":2944.0,"best_bid_price":2943.9,"best_bid_quantity":10,"best_offer_price":2944.1,"best_offer_quantity":12,"ltt":"25-Sep-2026 10:00:00"},
              {"exchange_code":"NSE","stock_code":"RELIND","ltp":2945.1,"best_bid_price":2945.05,"best_bid_quantity":120,"best_offer_price":2945.15,"best_offer_quantity":75,"ltt":"25-Sep-2026 10:00:01"}],
             "Status":200,"Error":null}
            """), "NSE")!;

        tick.Bid.Should().Be(2945.05);
        tick.TimestampUtc.Should().Be(new DateTime(2026, 9, 25, 4, 30, 1, DateTimeKind.Utc));
    }

    [Fact]
    public void Breeze_candles_are_ist_datetimes() =>
        RealIciciBreezeClient.ParseCandles(Json("""
            {"Success":[{"close":2945.0,"datetime":"2022-08-15 09:15:00","exchange_code":"NSE","high":2950.0,"low":2940.0,"open":2942.0,"stock_code":"RELIND","volume":12345}],"Status":200,"Error":null}
            """), 1.0).Single().TimestampUtc.Should().Be(new DateTime(2022, 8, 15, 3, 45, 0, DateTimeKind.Utc));
}
