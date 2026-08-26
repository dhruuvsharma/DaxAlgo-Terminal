using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Crypto;
using TradingTerminal.Infrastructure.Deribit;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Deribit's wire format, pinned against payloads captured from the live venue.
///
/// <para>Every fixture below is a real response, trimmed — the REST shapes from calling the public
/// endpoints and the socket shapes from connecting and subscribing. That matters more here than usual,
/// because two of the three things this venue does differently are things a reasonable guess gets
/// wrong <b>silently</b>: the code runs, parses nothing, and reports an empty market.</para>
/// </summary>
public sealed class DeribitWireFormatTests
{
    // ── candles: column-oriented ────────────────────────────────────────────────────────────────

    /// <summary>A real <c>get_tradingview_chart_data</c> response.</summary>
    private const string Candles = """
        {"usOut":1787748016599933,"usIn":1787748016596453,"usDiff":3480,"testnet":false,
         "result":{"volume":[223.43215858,106.33000615,262.52300625],
                   "ticks":[1787738400000,1787742000000,1787745600000],
                   "status":"ok",
                   "open":[78459.0,78740.5,78492.0],
                   "low":[78309.0,78309.5,77859.0],
                   "high":[78895.0,78792.0,78720.5],
                   "cost":[17567260.0,8354500.0,20592110.0],
                   "close":[78744.0,78491.5,78240.0]},
         "jsonrpc":"2.0"}
        """;

    [Fact]
    public void CandlesAreColumnsNotRows()
    {
        // The shape worth knowing before writing anything. Code written for the usual array-of-objects
        // finds no candles and reports an empty history — which from outside is indistinguishable from
        // a venue with nothing to say.
        using var json = JsonDocument.Parse(Candles);

        var bars = RealDeribitClient.ParseCandles(json.RootElement);

        bars.Should().HaveCount(3);
        bars[0].Open.Should().Be(78459.0d);
        bars[0].High.Should().Be(78895.0d);
        bars[0].Low.Should().Be(78309.0d);
        bars[0].Close.Should().Be(78744.0d);
    }

    [Fact]
    public void CandleTimestampsAreUnixMilliseconds()
    {
        using var json = JsonDocument.Parse(Candles);

        var first = RealDeribitClient.ParseCandles(json.RootElement)[0];

        first.TimestampUtc.Should().Be(
            DateTimeOffset.FromUnixTimeMilliseconds(1787738400000).UtcDateTime);
        first.TimestampUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void ColumnsAreZippedByIndexNotConcatenated()
    {
        // The failure this guards: reading the columns independently pairs a price with another bar's
        // timestamp, which produces a chart that is subtly and confidently wrong.
        using var json = JsonDocument.Parse(Candles);

        var bars = RealDeribitClient.ParseCandles(json.RootElement);

        bars[2].Close.Should().Be(78240.0d);
        bars[2].TimestampUtc.Should().Be(
            DateTimeOffset.FromUnixTimeMilliseconds(1787745600000).UtcDateTime);
    }

    [Fact]
    public void ARaggedResponseStopsRatherThanMispairing()
    {
        // Short columns mean the venue sent something inconsistent. Stopping is safer than pairing a
        // price with the wrong bar's time.
        using var json = JsonDocument.Parse("""
            {"result":{"status":"ok","ticks":[1,2,3],"open":[1.0],"high":[2.0],"low":[0.5],
                       "close":[1.5],"volume":[10.0]}}
            """);

        RealDeribitClient.ParseCandles(json.RootElement).Should().HaveCount(1);
    }

    [Fact]
    public void NoDataIsAnEmptyHistoryNotAnError()
    {
        // A legitimate answer for a window with no trading in it.
        using var json = JsonDocument.Parse("""{"result":{"status":"no_data","ticks":[]}}""");

        RealDeribitClient.ParseCandles(json.RootElement).Should().BeEmpty();
    }

    // ── the subscription envelope ───────────────────────────────────────────────────────────────

    /// <summary>A real subscribe acknowledgement — same socket, different envelope.</summary>
    private const string Acknowledgement = """
        {"jsonrpc":"2.0","id":1,"result":["trades.BTC-PERPETUAL.100ms","ticker.BTC-PERPETUAL.100ms"],
         "usIn":1787748068851095,"usOut":1787748068851268,"usDiff":173,"testnet":false}
        """;

    /// <summary>A real ticker push, trimmed to the fields that are read.</summary>
    private const string Ticker = """
        {"jsonrpc":"2.0","method":"subscription","params":{"channel":"ticker.BTC-PERPETUAL.100ms",
         "data":{"timestamp":1787748068838,"state":"open","index_price":78240.17,
                 "instrument_name":"BTC-PERPETUAL","last_price":78270.5,
                 "best_ask_price":78271.0,"best_bid_price":78270.5,
                 "best_ask_amount":12340.0,"best_bid_amount":56780.0}}}
        """;

    [Fact]
    public void ATickerPushBecomesATick()
    {
        using var json = JsonDocument.Parse(Ticker);

        var tick = RealDeribitClient.ParseTicker(json.RootElement).Should().ContainSingle().Subject;

        tick.Bid.Should().Be(78270.5d);
        tick.Ask.Should().Be(78271.0d);
        tick.BidSize.Should().Be(56780L);
        tick.AskSize.Should().Be(12340L);
    }

    [Fact]
    public void TheSubscribeAcknowledgementIsNotAQuote()
    {
        // It arrives on the same socket carrying "result" rather than "params". Reading it as data is
        // how a stream publishes one garbage tick per reconnect — and a reconnect is routine.
        using var json = JsonDocument.Parse(Acknowledgement);

        RealDeribitClient.ParseTicker(json.RootElement).Should().BeEmpty();
        RealDeribitClient.ParseTrades(json.RootElement).Should().BeEmpty();
    }

    [Fact]
    public void APushForAnotherChannelIsIgnored()
    {
        // One socket carries every subscription, so each parser has to check the channel it got.
        using var json = JsonDocument.Parse(Ticker);

        RealDeribitClient.ParseTrades(json.RootElement).Should().BeEmpty();
    }

    // ── trades ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A real trades push. The venue batches them.</summary>
    private const string Trades = """
        {"jsonrpc":"2.0","method":"subscription","params":{"channel":"trades.BTC-PERPETUAL.100ms",
         "data":[{"timestamp":1787748093321,"price":78295.5,"direction":"buy","amount":520.0,
                  "instrument_name":"BTC-PERPETUAL","trade_id":"442249308"},
                 {"timestamp":1787748093452,"price":78290.0,"direction":"sell","amount":130.0,
                  "instrument_name":"BTC-PERPETUAL","trade_id":"442249309"}]}}
        """;

    [Fact]
    public void TradesArriveBatched()
    {
        using var json = JsonDocument.Parse(Trades);

        RealDeribitClient.ParseTrades(json.RootElement).Should().HaveCount(2);
    }

    [Fact]
    public void DirectionIsTheAggressor()
    {
        // Which side crossed the spread, which is the only thing a tape is for.
        using var json = JsonDocument.Parse(Trades);

        var trades = RealDeribitClient.ParseTrades(json.RootElement).ToArray();

        trades[0].Aggressor.Should().Be(AggressorSide.Buy);
        trades[1].Aggressor.Should().Be(AggressorSide.Sell);
    }

    // ── the book ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A real book snapshot. Every level carries an action.</summary>
    private const string BookSnapshot = """
        {"jsonrpc":"2.0","method":"subscription","params":{"channel":"book.BTC-PERPETUAL.100ms",
         "data":{"timestamp":1787748091881,"type":"snapshot","change_id":171529216291,
                 "instrument_name":"BTC-PERPETUAL",
                 "bids":[["new",78295.0,2310.0],["new",78286.0,37490.0]],
                 "asks":[["new",78296.0,1500.0],["new",78300.0,9000.0]]}}}
        """;

    [Fact]
    public void ASnapshotBuildsTheBook()
    {
        using var json = JsonDocument.Parse(BookSnapshot);
        var book = new L2OrderBook();

        var depth = RealDeribitClient.ParseBook(json.RootElement, book, levels: 10)
            .Should().ContainSingle().Subject;

        depth.Bids[0].Price.Should().Be(78295.0d);
        depth.Asks[0].Price.Should().Be(78296.0d);
    }

    [Fact]
    public void DeleteRemovesALevelRatherThanResizingIt()
    {
        // Deribit states the action per level instead of using a size of zero to mean removal, which is
        // what most venues do. Treating a delete as a size update leaves the level in the book at its
        // old size, and the top of book slowly fills with prices that are no longer there.
        var book = new L2OrderBook();

        using var snapshot = JsonDocument.Parse(BookSnapshot);
        RealDeribitClient.ParseBook(snapshot.RootElement, book, 10).ToArray();

        using var update = JsonDocument.Parse("""
            {"jsonrpc":"2.0","method":"subscription","params":{"channel":"book.BTC-PERPETUAL.100ms",
             "data":{"timestamp":1787748092000,"type":"change",
                     "bids":[["delete",78295.0,0.0]],"asks":[]}}}
            """);

        var depth = RealDeribitClient.ParseBook(update.RootElement, book, 10)
            .Should().ContainSingle().Subject;

        depth.Bids.Should().NotContain(level => level.Price == 78295.0d);
        depth.Bids[0].Price.Should().Be(78286.0d);
    }

    [Fact]
    public void ASnapshotReplacesRatherThanAmends()
    {
        // Amending onto a stale book is how two sides of a spread end up crossed.
        var book = new L2OrderBook();

        using var first = JsonDocument.Parse(BookSnapshot);
        RealDeribitClient.ParseBook(first.RootElement, book, 10).ToArray();

        using var second = JsonDocument.Parse("""
            {"jsonrpc":"2.0","method":"subscription","params":{"channel":"book.BTC-PERPETUAL.100ms",
             "data":{"timestamp":1787748093000,"type":"snapshot",
                     "bids":[["new",70000.0,5.0]],"asks":[["new",70001.0,5.0]]}}}
            """);

        var depth = RealDeribitClient.ParseBook(second.RootElement, book, 10)
            .Should().ContainSingle().Subject;

        depth.Bids.Should().ContainSingle();
        depth.Bids[0].Price.Should().Be(70000.0d);
    }
}
