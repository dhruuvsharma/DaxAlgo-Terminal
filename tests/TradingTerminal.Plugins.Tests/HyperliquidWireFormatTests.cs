using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Hyperliquid;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Hyperliquid's wire format, pinned against payloads captured from the live venue.
///
/// <para>This venue departs from every other one in the tree in four ways, and the first is the one
/// that matters: the book identifies its two sides <b>by position</b> rather than by name. Code written
/// against the usual <c>bids</c>/<c>asks</c> object finds nothing, publishes nothing, and looks exactly
/// like a venue with an empty book.</para>
/// </summary>
public sealed class HyperliquidWireFormatTests
{
    private const double Scale = 10_000d;

    /// <summary>A real l2Book push, trimmed to three levels a side.</summary>
    private const string Book = """
        {"channel":"l2Book","data":{"coin":"BTC","time":1787749195414,
         "levels":[[{"px":"78316.0","sz":"0.75187","n":1},
                    {"px":"78314.0","sz":"0.00067","n":1},
                    {"px":"78311.0","sz":"0.00014","n":1}],
                   [{"px":"78320.0","sz":"1.20000","n":2},
                    {"px":"78322.0","sz":"0.50000","n":1},
                    {"px":"78325.0","sz":"0.10000","n":1}]]}}
        """;

    // ── the positional book ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheTwoSidesAreIdentifiedByPositionNotByName()
    {
        // levels[0] is bids and levels[1] is asks. There are no keys to read, which is why code written
        // against the usual shape finds an empty book rather than failing loudly.
        using var json = JsonDocument.Parse(Book);

        var depth = RealHyperliquidClient.ParseBook(json.RootElement, levels: 10, Scale)
            .Should().ContainSingle().Subject;

        depth.Bids[0].Price.Should().Be(78316.0d);
        depth.Asks[0].Price.Should().Be(78320.0d);
    }

    [Fact]
    public void TheBookIsNotCrossed()
    {
        // The check that catches the two sides being read the wrong way round: swap them and the best
        // bid sits above the best ask, which is a book no market ever shows.
        using var json = JsonDocument.Parse(Book);

        var depth = RealHyperliquidClient.ParseBook(json.RootElement, 10, Scale).Single();

        depth.Bids[0].Price.Should().BeLessThan(depth.Asks[0].Price);
    }

    [Fact]
    public void PricesAndSizesArriveAsStrings()
    {
        using var json = JsonDocument.Parse(Book);
        var level = json.RootElement.GetProperty("data").GetProperty("levels")[0][0];

        level.GetProperty("px").ValueKind.Should().Be(JsonValueKind.String);
        level.GetProperty("sz").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public void SizesAreScaledToWholeUnits()
    {
        // 0.75187 of a coin at a scale of ten thousand.
        using var json = JsonDocument.Parse(Book);

        var depth = RealHyperliquidClient.ParseBook(json.RootElement, 10, Scale).Single();

        depth.Bids[0].Size.Should().Be(7519L);
    }

    // ── top of book, derived ────────────────────────────────────────────────────────────────────

    [Fact]
    public void TopOfBookIsDerivedBecauseThereIsNoTickerChannel()
    {
        // The venue publishes no ticker. An L1 subscription here is an L2 subscription that yields only
        // the touch — which is worth doing rather than leaving the method empty, because a chart asking
        // for a quote should get the same number the book's first level carries.
        using var json = JsonDocument.Parse(Book);

        var tick = RealHyperliquidClient.ParseTouch(json.RootElement, Scale)
            .Should().ContainSingle().Subject;

        tick.Bid.Should().Be(78316.0d);
        tick.Ask.Should().Be(78320.0d);
        tick.BidSize.Should().Be(7519L);
    }

    [Fact]
    public void AnEmptySideYieldsNoQuote()
    {
        // Half a book is not a quote. Publishing one with a zero on one side would put a spread of the
        // entire price into the pipeline.
        using var json = JsonDocument.Parse("""
            {"channel":"l2Book","data":{"coin":"BTC","time":1,"levels":[[],[{"px":"1.0","sz":"1.0","n":1}]]}}
            """);

        RealHyperliquidClient.ParseTouch(json.RootElement, Scale).Should().BeEmpty();
    }

    // ── the subscription envelope ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheSubscribeAcknowledgementIsNotData()
    {
        // It arrives on the same socket. Read as data it yields one meaningless value per reconnect,
        // and reconnects are routine.
        using var json = JsonDocument.Parse("""
            {"channel":"subscriptionResponse","data":{"method":"subscribe",
             "subscription":{"type":"l2Book","coin":"BTC"}}}
            """);

        RealHyperliquidClient.ParseBook(json.RootElement, 10, Scale).Should().BeEmpty();
        RealHyperliquidClient.ParseTouch(json.RootElement, Scale).Should().BeEmpty();
        RealHyperliquidClient.ParseTrades(json.RootElement, Scale).Should().BeEmpty();
    }

    [Fact]
    public void APushForAnotherChannelIsIgnored()
    {
        using var json = JsonDocument.Parse(Book);

        RealHyperliquidClient.ParseTrades(json.RootElement, Scale).Should().BeEmpty();
    }

    // ── trades ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A real trades push.</summary>
    private const string Trades = """
        {"channel":"trades","data":[
          {"coin":"BTC","side":"B","px":"78324.0","sz":"0.00014","time":1787749176082,"tid":660835336978779},
          {"coin":"BTC","side":"A","px":"78320.0","sz":"0.39420","time":1787749176090,"tid":660835336978780}]}
        """;

    [Fact]
    public void TradeSideIsTheLetterOfTheBookSideThatWasHit()
    {
        // "B" and "A", not the buy/sell spelling every other venue uses. Getting this wrong inverts the
        // tape — and an inverted tape is worse than none, because cumulative delta still looks plausible.
        using var json = JsonDocument.Parse(Trades);

        var trades = RealHyperliquidClient.ParseTrades(json.RootElement, Scale).ToArray();

        trades.Should().HaveCount(2);
        trades[0].Aggressor.Should().Be(AggressorSide.Buy);
        trades[1].Aggressor.Should().Be(AggressorSide.Sell);
    }

    [Fact]
    public void TradeSizesAreScaledTheSameWayTheBookIs()
    {
        using var json = JsonDocument.Parse(Trades);

        RealHyperliquidClient.ParseTrades(json.RootElement, Scale).First().Size.Should().Be(1L);
    }

    // ── candles ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A real candleSnapshot response.</summary>
    private const string Candles = """
        [{"t":1787734800000,"T":1787738399999,"s":"BTC","i":"1h","o":"78691.0","c":"78433.0",
          "h":"78873.0","l":"78203.0","v":"2274.78672","n":19412},
         {"t":1787738400000,"T":1787741999999,"s":"BTC","i":"1h","o":"78432.0","c":"78700.0",
          "h":"78858.0","l":"78267.0","v":"1212.39731","n":13021}]
        """;

    [Fact]
    public void CandlesAreAFlatArrayOfObjects()
    {
        using var json = JsonDocument.Parse(Candles);

        var bars = RealHyperliquidClient.ParseCandles(json.RootElement, Scale);

        bars.Should().HaveCount(2);
        bars[0].Open.Should().Be(78691.0d);
        bars[0].Close.Should().Be(78433.0d);
    }

    [Fact]
    public void ACandleIsStampedWithItsOpenNotItsClose()
    {
        // The venue sends both: "t" opens the bar and "T" closes it. Stamping a bar with its close
        // shifts every series one interval into the future, which is the shape of a look-ahead bug.
        using var json = JsonDocument.Parse(Candles);

        RealHyperliquidClient.ParseCandles(json.RootElement, Scale)[0].TimestampUtc
            .Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1787734800000).UtcDateTime);
    }
}
