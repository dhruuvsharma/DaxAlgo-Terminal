using System.IO;
using System.Text;
using FluentAssertions;
using Google.Protobuf;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Upstox;
using Xunit;

namespace TradingTerminal.Tests.Infrastructure;

/// <summary>
/// Tests for the Upstox parsing helpers. The protobuf test encodes a <c>FeedResponse</c> by hand with
/// <see cref="CodedOutputStream"/> following the documented V3 field layout, then decodes it with
/// <see cref="UpstoxFeedDecoder"/> — proving the wire-format walker correctly traverses the nested
/// map / oneof / repeated structure for the field numbers the client targets.
/// </summary>
public sealed class UpstoxParsingTests
{
    [Fact]
    public void Decode_full_feed_yields_ltp_and_depth_levels()
    {
        // LTPC { ltp(1)=101.5 }
        var ltpc = Msg(c => WriteDouble(c, 1, 101.5));
        // Quote { bidQ(1), bidP(2), askQ(3), askP(4) }
        var lvl1 = Msg(c => { WriteInt64(c, 1, 10); WriteDouble(c, 2, 101.0); WriteInt64(c, 3, 12); WriteDouble(c, 4, 101.5); });
        var lvl2 = Msg(c => { WriteInt64(c, 1, 20); WriteDouble(c, 2, 100.5); WriteInt64(c, 3, 22); WriteDouble(c, 4, 102.0); });
        // MarketLevel { repeated Quote bidAskQuote(1) }
        var marketLevel = Msg(c => { WriteMessage(c, 1, lvl1); WriteMessage(c, 1, lvl2); });
        // MarketFullFeed { ltpc(1), marketLevel(2) }
        var marketFF = Msg(c => { WriteMessage(c, 1, ltpc); WriteMessage(c, 2, marketLevel); });
        // FullFeed { marketFF(1) }
        var fullFeed = Msg(c => WriteMessage(c, 1, marketFF));
        // Feed { fullFeed(2) }
        var feed = Msg(c => WriteMessage(c, 2, fullFeed));
        // feeds map entry { key(1)="NSE_EQ|TEST", value(2)=Feed }
        var entry = Msg(c => { WriteString(c, 1, "NSE_EQ|TEST"); WriteMessage(c, 2, feed); });
        // FeedResponse { feeds(2)=entry }
        var response = Msg(c => WriteMessage(c, 2, entry));

        var feeds = UpstoxFeedDecoder.Decode(response);

        feeds.Should().ContainSingle();
        var f = feeds[0];
        f.InstrumentKey.Should().Be("NSE_EQ|TEST");
        f.Ltp.Should().Be(101.5);
        f.Levels.Should().HaveCount(2);
        f.Levels[0].BidPrice.Should().Be(101.0);
        f.Levels[0].BidQty.Should().Be(10);
        f.Levels[0].AskPrice.Should().Be(101.5);
        f.Levels[1].AskPrice.Should().Be(102.0);
    }

    [Fact]
    public void Decode_index_ltpc_only_yields_ltp_and_no_levels()
    {
        var ltpc = Msg(c => WriteDouble(c, 1, 22150.25));
        var indexFF = Msg(c => WriteMessage(c, 1, ltpc));       // IndexFullFeed { ltpc(1) }
        var fullFeed = Msg(c => WriteMessage(c, 2, indexFF));   // FullFeed { indexFF(2) }
        var feed = Msg(c => WriteMessage(c, 2, fullFeed));
        var entry = Msg(c => { WriteString(c, 1, "NSE_INDEX|Nifty 50"); WriteMessage(c, 2, feed); });
        var response = Msg(c => WriteMessage(c, 2, entry));

        var feeds = UpstoxFeedDecoder.Decode(response);

        feeds.Should().ContainSingle();
        feeds[0].InstrumentKey.Should().Be("NSE_INDEX|Nifty 50");
        feeds[0].Ltp.Should().Be(22150.25);
        feeds[0].Levels.Should().BeEmpty();
    }

    [Fact]
    public void Decode_unknown_fields_are_skipped_gracefully()
    {
        // A FeedResponse carrying only an unrelated scalar field (e.g. currentTs) must not throw.
        var response = Msg(c => WriteInt64(c, 3, 1_700_000_000_000));
        var feeds = UpstoxFeedDecoder.Decode(response);
        feeds.Should().BeEmpty();
    }

    [Fact]
    public void ParseCandles_sorts_newest_first_response_into_ascending_bars()
    {
        // Upstox returns candles newest-first; the parser must sort ascending and convert IST → UTC.
        var json = """
        {
          "status": "success",
          "data": {
            "candles": [
              ["2024-06-13T15:29:00+05:30", 104.0, 106.0, 103.0, 105.0, 5000, 0],
              ["2024-06-13T15:28:00+05:30",  99.0, 101.0,  98.0, 100.0, 4000, 0]
            ]
          }
        }
        """;
        var bars = RealUpstoxClient.ParseCandles(Encoding.UTF8.GetBytes(json));

        bars.Should().HaveCount(2);
        bars[0].Open.Should().Be(99.0);   // earlier candle first after the ascending sort
        bars[1].Open.Should().Be(104.0);
        bars[0].TimestampUtc.Should().BeBefore(bars[1].TimestampUtc);
        bars[0].TimestampUtc.Should().Be(new DateTime(2024, 6, 13, 9, 58, 0, DateTimeKind.Utc)); // 15:28 IST
        bars[0].Volume.Should().Be(4000);
    }

    [Fact]
    public void ToInstrumentKey_passes_through_full_keys_and_composes_bare_symbols()
    {
        RealUpstoxClient.ToInstrumentKey(new Contract("NSE_EQ|INE002A01018", "EQ", "NSE", "INR", "NSE"))
            .Should().Be("NSE_EQ|INE002A01018");
        RealUpstoxClient.ToInstrumentKey(new Contract("RELIANCE", "EQ", "NSE", "INR", "NSE"))
            .Should().Be("NSE_EQ|RELIANCE");
    }

    // ── protobuf wire-format encode helpers (mirror the documented V3 schema) ────────────────────

    private static byte[] Msg(Action<CodedOutputStream> write)
    {
        using var ms = new MemoryStream();
        var cos = new CodedOutputStream(ms);
        write(cos);
        cos.Flush();
        return ms.ToArray();
    }

    private static void WriteMessage(CodedOutputStream c, int field, byte[] sub)
    {
        c.WriteTag(field, WireFormat.WireType.LengthDelimited);
        c.WriteBytes(ByteString.CopyFrom(sub));
    }

    private static void WriteString(CodedOutputStream c, int field, string s)
    {
        c.WriteTag(field, WireFormat.WireType.LengthDelimited);
        c.WriteString(s);
    }

    private static void WriteDouble(CodedOutputStream c, int field, double v)
    {
        c.WriteTag(field, WireFormat.WireType.Fixed64);
        c.WriteDouble(v);
    }

    private static void WriteInt64(CodedOutputStream c, int field, long v)
    {
        c.WriteTag(field, WireFormat.WireType.Varint);
        c.WriteInt64(v);
    }
}
