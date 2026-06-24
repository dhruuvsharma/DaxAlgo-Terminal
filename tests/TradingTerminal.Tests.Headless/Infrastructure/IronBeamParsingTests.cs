using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.IronBeam;
using Xunit;

namespace TradingTerminal.Tests.Infrastructure;

/// <summary>
/// Offline tests for the Ironbeam WebSocket JSON parsers and symbol mapping. No network — they feed
/// the pure deterministic helpers real-shaped payloads and assert the mapping into the canonical
/// domain records (and that unknown shapes return null rather than throwing).
/// </summary>
public sealed class IronBeamParsingTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static Contract C(string symbol, string exchange = "XCME") =>
        new(symbol, "FUT", exchange, "USD", exchange);

    // ── ToIronBeamSymbol ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToIronBeamSymbol_passes_through_when_symbol_already_qualified()
    {
        RealIronBeamClient.ToIronBeamSymbol(C("XCME:ES.U16")).Should().Be("XCME:ES.U16");
    }

    [Fact]
    public void ToIronBeamSymbol_composes_exchange_and_symbol()
    {
        RealIronBeamClient.ToIronBeamSymbol(C("ES", "XNYM")).Should().Be("XNYM:ES");
    }

    [Fact]
    public void ToIronBeamSymbol_defaults_exchange_to_XCME_when_blank()
    {
        RealIronBeamClient.ToIronBeamSymbol(C("ES", "  ")).Should().Be("XCME:ES");
    }

    // ── ParseQuote ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"s":"XCME:ES","b":4500.25,"a":4500.50,"bs":12,"as":7}""")]
    [InlineData("""{"s":"XCME:ES","bp":4500.25,"ap":4500.50,"bsz":12,"asz":7}""")]
    [InlineData("""{"s":"XCME:ES","bid":4500.25,"ask":4500.50,"bidSize":12,"askSize":7}""")]
    public void ParseQuote_resolves_bid_ask_price_and_size_variants(string json)
    {
        var tick = RealIronBeamClient.ParseQuote(Json(json));

        tick.Should().NotBeNull();
        tick!.Bid.Should().Be(4500.25);
        tick.Ask.Should().Be(4500.50);
        tick.BidSize.Should().Be(12);
        tick.AskSize.Should().Be(7);
    }

    [Fact]
    public void ParseQuote_falls_back_to_last_when_no_bid_ask()
    {
        var tick = RealIronBeamClient.ParseQuote(Json("""{"s":"XCME:ES","l":4501.0}"""));

        tick.Should().NotBeNull();
        tick!.Bid.Should().Be(4501.0);
        tick.Ask.Should().Be(4501.0);
    }

    [Fact]
    public void ParseQuote_parses_string_encoded_numbers()
    {
        var tick = RealIronBeamClient.ParseQuote(Json("""{"s":"XCME:ES","b":"4500.25","a":"4500.50"}"""));

        tick.Should().NotBeNull();
        tick!.Bid.Should().Be(4500.25);
        tick.Ask.Should().Be(4500.50);
    }

    [Fact]
    public void ParseQuote_returns_null_on_unknown_shape_without_throwing()
    {
        RealIronBeamClient.ParseQuote(Json("""{"s":"XCME:ES","foo":1}""")).Should().BeNull();
        RealIronBeamClient.ParseQuote(Json("\"ping\"")).Should().BeNull();
        RealIronBeamClient.ParseQuote(Json("[]")).Should().BeNull();
    }

    // ── ParseDepth ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseDepth_reads_array_levels_price_size()
    {
        var depth = RealIronBeamClient.ParseDepth(Json("""
            {"s":"XCME:ES","b":[[4500.25,10],[4500.00,20]],"a":[[4500.50,5],[4500.75,8]]}
            """));

        depth.Should().NotBeNull();
        depth!.Bids.Should().HaveCount(2);
        depth.Asks.Should().HaveCount(2);
        depth.BestBid.Should().Be(4500.25);
        depth.BestBidSize.Should().Be(10);
        depth.BestAsk.Should().Be(4500.50);
        depth.BestAskSize.Should().Be(5);
    }

    [Fact]
    public void ParseDepth_reads_object_levels_p_sz()
    {
        var depth = RealIronBeamClient.ParseDepth(Json("""
            {"s":"XCME:ES","bids":[{"p":4500.25,"sz":10}],"asks":[{"p":4500.50,"sz":5}]}
            """));

        depth.Should().NotBeNull();
        depth!.Bids.Should().ContainSingle();
        depth.BestBid.Should().Be(4500.25);
        depth.BestBidSize.Should().Be(10);
        depth.BestAsk.Should().Be(4500.50);
        depth.BestAskSize.Should().Be(5);
    }

    [Fact]
    public void ParseDepth_returns_null_on_unknown_shape_without_throwing()
    {
        RealIronBeamClient.ParseDepth(Json("""{"s":"XCME:ES","foo":1}""")).Should().BeNull();
        RealIronBeamClient.ParseDepth(Json("\"ping\"")).Should().BeNull();
    }

    // ── ParseTrade ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("\"B\"", AggressorSide.Buy)]
    [InlineData("\"S\"", AggressorSide.Sell)]
    [InlineData("\"buy\"", AggressorSide.Buy)]
    [InlineData("\"sell\"", AggressorSide.Sell)]
    [InlineData("1", AggressorSide.Buy)]
    [InlineData("-1", AggressorSide.Sell)]
    public void ParseTrade_resolves_direction_from_td(string td, AggressorSide expected)
    {
        var trade = RealIronBeamClient.ParseTrade(Json($$"""{"s":"XCME:ES","p":4500.25,"sz":3,"td":{{td}}}"""));

        trade.Should().NotBeNull();
        trade!.Price.Should().Be(4500.25);
        trade.Size.Should().Be(3);
        trade.Aggressor.Should().Be(expected);
    }

    [Fact]
    public void ParseTrade_missing_direction_is_unknown()
    {
        var trade = RealIronBeamClient.ParseTrade(Json("""{"s":"XCME:ES","p":4500.25,"sz":3}"""));

        trade.Should().NotBeNull();
        trade!.Aggressor.Should().Be(AggressorSide.Unknown);
    }

    [Fact]
    public void ParseTrade_returns_null_when_no_price_without_throwing()
    {
        RealIronBeamClient.ParseTrade(Json("""{"s":"XCME:ES","sz":3}""")).Should().BeNull();
        RealIronBeamClient.ParseTrade(Json("\"ping\"")).Should().BeNull();
    }

    [Fact]
    public void ParseTrade_treats_large_timestamp_as_epoch_millis()
    {
        const long ms = 1_700_000_000_123;
        var trade = RealIronBeamClient.ParseTrade(Json($$"""{"s":"XCME:ES","p":4500.25,"sz":1,"st":{{ms}}}"""));

        trade.Should().NotBeNull();
        trade!.TimestampUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime);
    }

    [Fact]
    public void ParseTrade_treats_small_timestamp_as_epoch_seconds()
    {
        const long s = 1_700_000_000;
        var trade = RealIronBeamClient.ParseTrade(Json($$"""{"s":"XCME:ES","p":4500.25,"sz":1,"st":{{s}}}"""));

        trade.Should().NotBeNull();
        trade!.TimestampUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime);
    }
}
