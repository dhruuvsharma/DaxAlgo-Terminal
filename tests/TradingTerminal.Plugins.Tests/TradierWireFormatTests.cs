using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Infrastructure.Tradier;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Tradier's response shapes.
///
/// <para><b>The trap.</b> A request for one symbol returns <c>quotes.quote</c> as an object; a request
/// for several returns it as an array. A parser written for one silently finds nothing in the other —
/// no exception, no log line, just an empty chart that looks like a closed market. Both are pinned
/// here, because the failure has no symptom that would ever lead anyone back to this code.</para>
///
/// <para>These run without a token: reading a payload is pure, and the payload shape is the half that
/// documentation gets wrong.</para>
/// </summary>
public sealed class TradierWireFormatTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void One_symbol_returns_quote_as_an_object()
    {
        var ticks = RealTradierClient.ParseQuote(Json("""
        {
          "quotes": {
            "quote": {
              "symbol": "AAPL", "last": 213.25, "bid": 213.20, "ask": 213.30,
              "volume": 41234567, "trade_date": 1756209600000
            }
          }
        }
        """)).ToList();

        ticks.Should().ContainSingle();
        ticks[0].Bid.Should().Be(213.20);
        ticks[0].Ask.Should().Be(213.30);
    }

    [Fact]
    public void Several_symbols_return_quote_as_an_array()
    {
        var ticks = RealTradierClient.ParseQuote(Json("""
        {
          "quotes": {
            "quote": [
              { "symbol": "AAPL", "last": 213.25, "bid": 213.20, "ask": 213.30 },
              { "symbol": "MSFT", "last": 442.10, "bid": 442.05, "ask": 442.15 }
            ]
          }
        }
        """)).ToList();

        // Two quotes come back from the array shape. Tick carries no symbol — the subscription
        // supplies the contract — so what matters here is that neither entry is dropped.
        ticks.Should().HaveCount(2);
        ticks.Select(t => t.Bid).Should().Equal(213.20, 442.05);
    }

    [Fact]
    public void An_unmatched_symbol_yields_nothing_rather_than_a_zero_priced_tick()
    {
        // Tradier answers an unknown symbol with a null quote. A tick at zero would be worse than no
        // tick: it would print on the tape and drag any average that consumed it.
        var ticks = RealTradierClient.ParseQuote(Json("""
        { "quotes": { "quote": null, "unmatched_symbols": { "symbol": "NOPE" } } }
        """)).ToList();

        ticks.Should().BeEmpty();
    }

    [Fact]
    public void History_reads_the_daily_bars()
    {
        var bars = RealTradierClient.ParseHistory(Json("""
        {
          "history": {
            "day": [
              { "date": "2026-08-24", "open": 210.0, "high": 214.5, "low": 209.5,
                "close": 213.25, "volume": 41234567 },
              { "date": "2026-08-25", "open": 213.5, "high": 216.0, "low": 212.0,
                "close": 215.75, "volume": 38765432 }
            ]
          }
        }
        """));

        bars.Should().HaveCount(2);
        bars[0].High.Should().Be(214.5);
        bars[1].Close.Should().Be(215.75);
        bars[0].TimestampUtc.Should().BeBefore(
            bars[1].TimestampUtc, "bars are handed over in time order");
    }

    [Fact]
    public void A_single_day_of_history_is_an_object_too()
    {
        // The same object-or-array shape, on the history endpoint. Asking for one day is what a
        // freshly-opened chart does, so this is the common path rather than the edge case.
        var bars = RealTradierClient.ParseHistory(Json("""
        {
          "history": {
            "day": { "date": "2026-08-25", "open": 213.5, "high": 216.0,
                     "low": 212.0, "close": 215.75, "volume": 38765432 }
          }
        }
        """));

        bars.Should().ContainSingle();
        bars[0].Close.Should().Be(215.75);
    }

    [Fact]
    public void An_empty_history_window_is_read_as_no_bars()
    {
        // A weekend or a holiday range. Tradier returns history: null, which must be empty rather
        // than an exception on a chart that simply asked for a quiet week.
        RealTradierClient.ParseHistory(Json("""{ "history": null }""")).Should().BeEmpty();
    }
}
