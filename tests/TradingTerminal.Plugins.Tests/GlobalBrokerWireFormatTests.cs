using System.Text;
using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.ETrade;
using TradingTerminal.Infrastructure.Ig;
using TradingTerminal.Infrastructure.Questrade;
using TradingTerminal.Infrastructure.RobinhoodCrypto;
using TradingTerminal.Infrastructure.Saxo;
using TradingTerminal.Infrastructure.Schwab;
using TradingTerminal.Infrastructure.Tastytrade;
using TradingTerminal.Infrastructure.TradeStation;
using TradingTerminal.Infrastructure.Tradovate;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The response shapes the US and global adapters read, pinned. None of these brokers could be run with an
/// account while writing them, so each shape here is the documentation's (or, where marked, an assumption
/// the documentation did not settle) — the half of an adapter that can be proven without one.
/// </summary>
public sealed class GlobalBrokerWireFormatTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static readonly DateTime Minute = new(2026, 9, 25, 14, 30, 0, DateTimeKind.Utc);
    private static readonly long MinuteMs = new DateTimeOffset(Minute).ToUnixTimeMilliseconds();

    // ── Schwab ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Schwab_reads_candles_and_the_streamer_address()
    {
        RealSchwabClient.ParseCandles(Json($$"""{"candles":[{"open":1,"high":2,"low":0.5,"close":1.5,"volume":100,"datetime":{{MinuteMs}}}],"symbol":"AAPL","empty":false}"""))
            .Should().Equal(new Bar(Minute, 1, 2, 0.5, 1.5, 100));

        RealSchwabClient.ParseStreamerInfo(Json("""{"streamerInfo":[{"streamerSocketUrl":"wss://streamer-api.schwab.com/ws","schwabClientCustomerId":"cust","schwabClientCorrelId":"corr","schwabClientChannel":"N9","schwabClientFunctionId":"APIAPP"}]}"""))
            .Should().Be(new RealSchwabClient.StreamerInfo("wss://streamer-api.schwab.com/ws", "cust", "corr", "N9", "APIAPP"));
    }

    [Fact]
    public void Schwab_merges_level_one_deltas_and_reads_whole_books()
    {
        var state = new Dictionary<string, double[]>();
        var first = RealSchwabClient.Decode(Encoding.UTF8.GetBytes(
            $$"""{"data":[{"service":"LEVELONE_EQUITIES","timestamp":{{MinuteMs}},"command":"SUBS","content":[{"key":"AAPL","1":230.1,"2":230.2,"4":300,"5":500,"34":{{MinuteMs}}}]}]}"""), state, 1).ToList();
        first.Should().ContainSingle().Which.Value.Quote.Should().Be(new Tick(Minute, 230.1, 230.2, 300, 500));

        // A later update carries only the bid; the ask side is remembered.
        var second = RealSchwabClient.Decode(Encoding.UTF8.GetBytes(
            """{"data":[{"service":"LEVELONE_EQUITIES","timestamp":1,"content":[{"key":"AAPL","1":230.15}]}]}"""), state, 1).Single();
        second.Key.Should().Be("Q:AAPL");
        second.Value.Quote!.Bid.Should().Be(230.15);
        second.Value.Quote.Ask.Should().Be(230.2);

        var book = RealSchwabClient.Decode(Encoding.UTF8.GetBytes(
            $$"""{"data":[{"service":"NASDAQ_BOOK","timestamp":1,"content":[{"key":"AAPL","1":{{MinuteMs}},"2":[{"0":230.1,"1":400,"2":2,"3":[]}],"3":[{"0":230.2,"1":100,"2":1}]}]}]}"""), state, 1).Single();
        book.Key.Should().Be("B:AAPL");
        book.Value.Book.Should().BeEquivalentTo(new DepthSnapshot(Minute, [new DepthLevel(230.1, 400)], [new DepthLevel(230.2, 100)]));
    }

    [Fact]
    public void Schwab_logs_in_before_subscribing_and_groups_subscriptions_by_service()
    {
        var info = new RealSchwabClient.StreamerInfo("wss://x", "cust", "corr", "N9", "APIAPP");
        using var login = JsonDocument.Parse(RealSchwabClient.LoginMessage(info, "tok", 1));
        var request = login.RootElement.GetProperty("requests")[0];
        request.GetProperty("command").GetString().Should().Be("LOGIN");
        request.GetProperty("parameters").GetProperty("Authorization").GetString().Should().Be("tok", "the bare token, no Bearer prefix");

        var id = 1;
        using var subs = JsonDocument.Parse(RealSchwabClient.SubscribeMessage(info, ["Q:AAPL", "B:AAPL", "Q:MSFT"], "ADD", "NASDAQ_BOOK", () => ++id));
        var requests = subs.RootElement.GetProperty("requests").EnumerateArray().ToList();
        requests.Select(r => (r.GetProperty("service").GetString(), r.GetProperty("parameters").GetProperty("keys").GetString()))
            .Should().BeEquivalentTo([("LEVELONE_EQUITIES", "AAPL,MSFT"), ("NASDAQ_BOOK", "AAPL")]);

        RealSchwabClient.LoginResult("""{"response":[{"service":"ADMIN","command":"LOGIN","requestid":"1","content":{"code":0,"msg":"ok"}}]}"""u8.ToArray())
            .Should().Be(0);
        RealSchwabClient.LoginResult("""{"notify":[{"heartbeat":"1"}]}"""u8.ToArray()).Should().BeNull();
    }

    // ── TradeStation ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TradeStation_bars_are_stamped_with_their_close_and_shifted_to_their_start()
    {
        RealTradeStationClient.ParseBars(Json("""{"Bars":[{"High":"2","Low":"0.5","Open":"1","Close":"1.5","TimeStamp":"2026-09-25T14:35:00Z","TotalVolume":"900"}]}"""), TimeSpan.FromMinutes(5))
            .Should().Equal(new Bar(Minute, 1, 2, 0.5, 1.5, 900));

        RealTradeStationClient.ParseBars(Json("""{"Bars":[{"High":"2","Low":"1","Open":"1","Close":"2","TimeStamp":"2026-09-25T20:00:00Z","TotalVolume":"5"}]}"""), TimeSpan.FromDays(1))
            .Single().TimestampUtc.Should().Be(Minute.Date, "a daily bar belongs to its own date");

        RealTradeStationClient.Interval(BarSize.FifteenMinutes).Should().Be("interval=15&unit=Minute");
    }

    [Fact]
    public void TradeStation_quote_stream_merges_changes_and_skips_heartbeats()
    {
        var quote = new RealTradeStationClient.QuoteState();
        quote.Apply(Json("""{"Heartbeat":1,"Timestamp":"2026-09-25T14:30:00Z"}"""), 1).Should().BeNull();
        quote.Apply(Json("""{"Symbol":"SPY","Bid":"600.1","Ask":"600.2","BidSize":"300","AskSize":"200"}"""), 1)!
            .Should().Match<Tick>(t => t.Bid == 600.1 && t.Ask == 600.2 && t.BidSize == 300 && t.AskSize == 200);
        quote.Apply(Json("""{"Symbol":"SPY","Ask":"600.3"}"""), 1)!
            .Should().Match<Tick>(t => t.Bid == 600.1 && t.Ask == 600.3 && t.BidSize == 300);
    }

    [Fact]
    public void TradeStation_reads_the_aggregated_book()
    {
        var book = RealTradeStationClient.ParseDepth(Json(
            """{"Bids":[{"Side":"Bid","Price":"600.1","TotalSize":"1200","LatestTime":"2026-09-25T14:30:00Z"}],"Asks":[{"Side":"Ask","Price":"600.2","TotalSize":"800"}]}"""), 1)!;
        book.Should().BeEquivalentTo(new DepthSnapshot(Minute, [new DepthLevel(600.1, 1200)], [new DepthLevel(600.2, 800)]));
        RealTradeStationClient.ParseDepth(Json("""{"Heartbeat":2}"""), 1).Should().BeNull();
    }

    // ── tastytrade / DXLink ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void DXLink_compact_data_decodes_quotes_prints_and_candles()
    {
        var frame = Encoding.UTF8.GetBytes($$"""
            {"type":"FEED_DATA","channel":1,"data":[
              "Quote",["Quote","SPY",600.1,600.2,300,200,{{MinuteMs}},0,"Quote","QQQ","NaN",500.2,0,1,0,0],
              "TimeAndSale",["TimeAndSale","SPY",{{MinuteMs}},600.15,25,"BUY","TimeAndSale","SPY",{{MinuteMs}},600.1,5,"UNDEFINED"],
              "Candle",["Candle","SPY{=5m}",{{MinuteMs}},600,601,599,600.5,12345,8]]}
            """);

        var events = RealTastytradeClient.Decode(frame);

        events.Should().HaveCount(4, "the QQQ quote with no bid is dropped");
        events[0].Quote.Should().Be(new Tick(Minute, 600.1, 600.2, 300, 200));
        events[1].Trade.Should().Be(new TradeTick(Minute, 600.15, 25, AggressorSide.Buy));
        events[2].Trade!.Aggressor.Should().Be(AggressorSide.Unknown);
        events[3].Should().Match<DxEvent>(e => e.Symbol == "SPY{=5m}" && e.Candle == new Bar(Minute, 600, 601, 599, 600.5, 12345)
            && (e.Flags & RealTastytradeClient.SnapshotEnd) != 0);
    }

    [Fact]
    public void DXLink_candle_symbols_and_subscriptions()
    {
        RealTastytradeClient.CandleSymbol("SPY", BarSize.OneHour).Should().Be("SPY{=1h}");
        RealTastytradeClient.CandleStep("SPY{=3m}").Should().Be(TimeSpan.FromMinutes(3));
        RealTastytradeClient.CandleStep("SPY").Should().BeNull();

        using var add = JsonDocument.Parse(RealTastytradeClient.Subscription("add", ["Quote|SPY", "Candle|SPY{=5m}"], new DateTimeOffset(Minute.AddMinutes(7))));
        var items = add.RootElement.GetProperty("add").EnumerateArray().ToList();
        items[0].TryGetProperty("fromTime", out _).Should().BeFalse();
        items[1].GetProperty("fromTime").GetInt64().Should().Be(MinuteMs, "a candle starts one bar before the forming one");

        RealTastytradeClient.HandshakeStep("""{"type":"AUTH_STATE","channel":0,"state":"AUTHORIZED","userId":"u"}"""u8.ToArray()).Should().Be("AUTHORIZED");
        RealTastytradeClient.HandshakeStep("""{"type":"AUTH_STATE","channel":0,"state":"UNAUTHORIZED"}"""u8.ToArray()).Should().BeNull();
        RealTastytradeClient.HandshakeStep("""{"type":"CHANNEL_OPENED","channel":1,"service":"FEED"}"""u8.ToArray()).Should().Be("CHANNEL_OPENED");
        RealTastytradeClient.ParseQuoteToken(Json("""{"data":{"token":"tok","dxlink-url":"wss://tasty-openapi-ws.dxfeed.com/realtime","level":"api"},"context":"/api-quote-tokens"}"""))
            .Should().Be(("tok", "wss://tasty-openapi-ws.dxfeed.com/realtime"));
    }

    // ── E*TRADE ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ETrade_reads_the_all_block_of_a_quote()
    {
        var epoch = new DateTimeOffset(Minute).ToUnixTimeSeconds();
        RealETradeClient.ParseQuote(Json("""{"QuoteResponse":{"QuoteData":[{"dateTimeUTC":EPOCH,"quoteStatus":"REALTIME","All":{"bid":229.9,"ask":230.1,"bidSize":100,"askSize":300,"lastTrade":230.0,"totalVolume":1234567},"Product":{"symbol":"AAPL","securityType":"EQ"}}]}}""".Replace("EPOCH", epoch.ToString(System.Globalization.CultureInfo.InvariantCulture))), 1)
            .Should().Be(new PolledQuote(Minute, 229.9, 230.1, 100, 300, 230.0, 1234567));
        RealETradeClient.ParseQuote(Json("""{"QuoteResponse":{"Messages":{"Message":[{"description":"Invalid Symbol","code":1019}]}}}"""), 1).Should().BeNull();
    }

    // ── Tradovate ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tradovate_frames_decode_quotes_by_contract_and_the_dom()
    {
        var state = new Dictionary<long, double[]>();
        var quote = RealTradovateClient.Decode(Encoding.UTF8.GetBytes(
            """a[{"e":"md","d":{"quotes":[{"timestamp":"2026-09-25T14:30:00.000Z","contractId":2106323,"entries":{"Bid":{"price":6500.25,"size":33},"Offer":{"price":6500.5,"size":61},"Trade":{"price":6500.25,"size":1}}}]}}]"""), state, 1).Single();
        quote.Key.Should().Be("q:2106323");
        quote.Value.Quote.Should().Be(new Tick(Minute, 6500.25, 6500.5, 33, 61));

        var dom = RealTradovateClient.Decode(Encoding.UTF8.GetBytes(
            """a[{"e":"md","d":{"doms":[{"contractId":2106323,"timestamp":"2026-09-25T14:30:00.000Z","bids":[{"price":6500.25,"size":33},{"price":6500,"size":80}],"offers":[{"price":6500.5,"size":61}]}]}}]"""), state, 1).Single();
        dom.Key.Should().Be("d:2106323");
        dom.Value.Book!.Bids.Should().HaveCount(2);

        RealTradovateClient.Messages("o"u8.ToArray()).Should().BeEmpty();
        RealTradovateClient.Messages("h"u8.ToArray()).Should().BeEmpty();
        RealTradovateClient.Frame("md/subscribeQuote", 3, """{"symbol":2106323}""").Should().Be("md/subscribeQuote\n3\n\n{\"symbol\":2106323}");
        RealTradovateClient.Subscribe("d:2106323", on: false).Should().Be(("md/unsubscribeDOM", """{"symbol":2106323}"""));
    }

    [Fact]
    public void Tradovate_charts_sum_up_and_down_volume_and_mark_the_end_of_history()
    {
        var message = RealTradovateClient.Messages(Encoding.UTF8.GetBytes(
            """a[{"e":"chart","d":{"charts":[{"id":9,"td":20260925,"bars":[{"timestamp":"2026-09-25T14:30:00.000Z","open":1,"high":2,"low":0.5,"close":1.5,"upVolume":40,"downVolume":60,"upTicks":3,"downTicks":4}]},{"id":9,"eoh":true}]}}]"""))
            .Single();
        var (bars, eoh) = RealTradovateClient.ParseChart(message);
        bars.Should().Equal(new Bar(Minute, 1, 2, 0.5, 1.5, 100));
        eoh.Should().BeTrue();

        using var request = JsonDocument.Parse(RealTradovateClient.ChartRequest("ESZ6", BarSize.FifteenMinutes, 50));
        request.RootElement.GetProperty("chartDescription").GetProperty("elementSize").GetInt32().Should().Be(15);
        request.RootElement.GetProperty("timeRange").GetProperty("asMuchAsElements").GetInt32().Should().Be(50);
    }

    // ── Saxo ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Saxo_instruments_are_asset_type_then_symbol()
    {
        RealSaxoClient.Split("Stock:AAPL:xnas").Should().Be(("Stock", "AAPL:xnas"));
        RealSaxoClient.Split("EURUSD").Should().Be(("FxSpot", "EURUSD"));
        RealSaxoClient.PickUic(Json("""{"Data":[{"Identifier":1,"Symbol":"EURUSD.x"},{"Identifier":21,"Symbol":"EURUSD","AssetType":"FxSpot"}]}"""), "EURUSD")
            .Should().Be(21);
    }

    [Fact]
    public void Saxo_reads_info_prices_depth_and_mid_bars()
    {
        RealSaxoClient.ParseQuote(Json("""{"Quote":{"Bid":1.1,"Ask":1.1002,"BidSize":1000000,"AskSize":2000000,"Mid":1.1001},"LastUpdated":"2026-09-25T14:30:00.000000Z"}"""), 1)
            .Should().Be(new Tick(Minute, 1.1, 1.1002, 1_000_000, 2_000_000));

        RealSaxoClient.ParseDepth(Json("""{"MarketDepth":{"Bid":[1.1,1.0999],"BidSize":[1,2],"Ask":[1.1002],"AskSize":[3],"NoOfBids":2,"NoOfOffers":1},"LastUpdated":"2026-09-25T14:30:00Z"}"""), 1)!
            .Bids.Should().Equal(new DepthLevel(1.1, 1), new DepthLevel(1.0999, 2));

        RealSaxoClient.ParseChart(Json("""{"Data":[{"Time":"2026-09-25T14:30:00.000000Z","OpenBid":1.0,"OpenAsk":1.25,"HighBid":2.0,"HighAsk":2.25,"LowBid":0.5,"LowAsk":0.75,"CloseBid":1.5,"CloseAsk":1.75}]}"""))
            .Should().Equal(new Bar(Minute, 1.125, 2.125, 0.625, 1.625, 0));
        RealSaxoClient.ParseChart(Json("""{"Data":[{"Time":"2026-09-25T14:30:00Z","Open":1,"High":2,"Low":0.5,"Close":1.5,"Volume":700}]}"""))
            .Should().Equal(new Bar(Minute, 1, 2, 0.5, 1.5, 700));
    }

    // ── IG ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IG_reads_snapshots_prices_and_allowance()
    {
        RealIgClient.ParseSnapshot(Json("""{"instrument":{"epic":"CS.D.EURUSD.CFD.IP"},"snapshot":{"marketStatus":"TRADEABLE","bid":11000.5,"offer":11001.1,"updateTime":"14:30:00"}}"""), Minute)
            .Should().Be(new PolledQuote(Minute, 11000.5, 11001.1, 0, 0));

        var prices = Json("""{"prices":[{"snapshotTime":"2026/09/25 15:30:00","snapshotTimeUTC":"2026-09-25T14:30:00","openPrice":{"bid":1,"ask":1.25,"lastTraded":null},"highPrice":{"bid":2,"ask":2.25},"lowPrice":{"bid":0.5,"ask":0.75},"closePrice":{"bid":1.5,"ask":1.75},"lastTradedVolume":42}],"metadata":{"allowance":{"remainingAllowance":9950,"totalAllowance":10000,"allowanceExpiry":500000}}}""");
        RealIgClient.ParsePrices(prices).Should().Equal(new Bar(Minute, 1.125, 2.125, 0.625, 1.625, 42));
        RealIgClient.Allowance(prices).Should().Be(9950);

        RealIgClient.CurrencyPair("CS.D.EURUSD.CFD.IP").Should().Be("EURUSD");
        RealIgClient.CurrencyPair("CS.D.USCGC.TODAY.IP").Should().BeNull("spot gold is not a pair");
        RealIgClient.CurrencyPair("IX.D.SPTRD.DAILY.IP").Should().BeNull();
    }

    [Fact]
    public void IG_session_comes_from_the_headers_and_a_refusal_from_the_error_code()
    {
        var session = IgSignIn.Read(200, """{"currentAccountId":"ABC12","accountType":"CFD"}""", "cst-1", "sec-1", "https://demo-api.ig.com/gateway/deal", DateTimeOffset.UnixEpoch);
        session.Should().Match<KeptSession>(s => s.AccessToken == "cst-1" && s.Secret == "sec-1" && s.Extra == "ABC12");

        FluentActions.Invoking(() => IgSignIn.Read(401, """{"errorCode":"error.security.invalid-details"}""", null, null, "h", DateTimeOffset.UnixEpoch))
            .Should().Throw<InvalidOperationException>().WithMessage("*error.security.invalid-details*");
    }

    // ── Questrade ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Questrade_picks_the_exact_symbol_and_reads_quotes_and_candles()
    {
        RealQuestradeClient.PickSymbolId(Json("""{"symbols":[{"symbol":"RYAAY","symbolId":1},{"symbol":"RY.TO","symbolId":34658,"listingExchange":"TSX"}]}"""), "RY.TO")
            .Should().Be(34658);
        RealQuestradeClient.PickSymbolId(Json("""{"symbols":[{"symbol":"RYAAY","symbolId":1}]}"""), "RY").Should().BeNull("a prefix match is not the symbol");

        RealQuestradeClient.ParseQuote(Json("""{"quotes":[{"symbol":"AAPL","symbolId":8049,"bidPrice":229.9,"bidSize":2,"askPrice":230.1,"askSize":4,"lastTradePrice":230,"delay":0}]}"""), 1)!
            .Should().Match<Tick>(t => t.Bid == 229.9 && t.Ask == 230.1 && t.BidSize == 2 && t.AskSize == 4);

        RealQuestradeClient.ParseCandles(Json("""{"candles":[{"start":"2026-09-25T10:30:00.000000-04:00","end":"2026-09-25T10:31:00.000000-04:00","low":0.5,"high":2,"open":1,"close":1.5,"volume":300}]}"""))
            .Should().Equal(new Bar(Minute, 1, 2, 0.5, 1.5, 300));
    }

    // ── Robinhood ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Robinhood_best_bid_ask_is_spread_inclusive()
    {
        RealRobinhoodCryptoClient.ParseBestBidAsk(Json("""{"results":[{"symbol":"BTC-USD","price":100000.5,"bid_inclusive_of_sell_spread":"99900.1","sell_spread":"0.001","ask_inclusive_of_buy_spread":"100100.9","buy_spread":"0.001","timestamp":"2026-09-25T14:30:00Z"}]}"""))
            .Should().Be(new PolledQuote(Minute, 99900.1, 100100.9, 0, 0, Last: 100000.5));
    }
}
