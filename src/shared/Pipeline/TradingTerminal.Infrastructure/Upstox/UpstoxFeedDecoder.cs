using Google.Protobuf;

namespace TradingTerminal.Infrastructure.Upstox;

/// <summary>
/// One bid/ask level decoded from the feed's <c>Quote</c> message (bid/ask price + quantity).
/// </summary>
internal readonly record struct UpstoxLevel(double BidPrice, long BidQty, double AskPrice, long AskQty);

/// <summary>
/// One instrument's decoded market-data snapshot: its Upstox instrument key, the last traded price
/// (when present), and up to five bid/ask depth levels (top level = L1).
/// </summary>
internal sealed class UpstoxFeed
{
    public string InstrumentKey { get; set; } = string.Empty;
    public double? Ltp { get; set; }
    public List<UpstoxLevel> Levels { get; } = new();
}

/// <summary>
/// Minimal, dependency-light decoder for the Upstox V3 market-data <c>FeedResponse</c> protobuf
/// message. Rather than vendoring the full <c>.proto</c> + a <c>Grpc.Tools</c> build step, this walks
/// the protobuf <b>wire format</b> directly with <see cref="CodedInputStream"/> (from the already-
/// referenced <c>Google.Protobuf</c> runtime), reading only the fields the terminal needs: per
/// instrument key, the last traded price and the 5-level bid/ask book.
///
/// <para>Message/field layout (Upstox <c>com.upstox.marketdatafeederv3udapi.rpc.proto</c>, V3):</para>
/// <list type="bullet">
/// <item><c>FeedResponse</c>: 2 = map&lt;string, Feed&gt; feeds.</item>
/// <item><c>Feed</c>: 1 = LTPC ltpc; 2 = FullFeed fullFeed; 3 = FirstLevelWithGreeks firstLevelWithGreeks.</item>
/// <item><c>FullFeed</c>: 1 = MarketFullFeed marketFF; 2 = IndexFullFeed indexFF.</item>
/// <item><c>MarketFullFeed</c>: 1 = LTPC ltpc; 2 = MarketLevel marketLevel.</item>
/// <item><c>IndexFullFeed</c>: 1 = LTPC ltpc.</item>
/// <item><c>MarketLevel</c>: 1 = repeated Quote bidAskQuote.</item>
/// <item><c>Quote</c>: 1 = int64 bidQ; 2 = double bidP; 3 = int64 askQ; 4 = double askP.</item>
/// <item><c>LTPC</c>: 1 = double ltp.</item>
/// </list>
///
/// <para><b>Validation note:</b> the field numbers above are from Upstox's published V3 schema and
/// have not been verified against a live session in this build. If the feed ever decodes to empty
/// L1/depth despite traffic, re-check these numbers against the current <c>MarketDataFeedV3.proto</c>.
/// Unknown fields are skipped, so a partial mismatch degrades gracefully rather than throwing.</para>
/// </summary>
internal static class UpstoxFeedDecoder
{
    private const int WireVarint = 0;
    private const int WireFixed64 = 1;
    private const int WireLengthDelimited = 2;

    /// <summary>Decodes one binary <c>FeedResponse</c> frame into per-instrument snapshots.</summary>
    public static IReadOnlyList<UpstoxFeed> Decode(byte[] data)
    {
        var result = new List<UpstoxFeed>();
        var input = new CodedInputStream(data);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            // FeedResponse: field 2 = one feeds-map entry (repeated message).
            if (FieldOf(tag) == 2 && WireOf(tag) == WireLengthDelimited)
            {
                var entry = input.ReadBytes().ToByteArray();
                var feed = ParseMapEntry(entry);
                if (feed is not null) result.Add(feed);
            }
            else
            {
                input.SkipLastField();
            }
        }
        return result;
    }

    /// <summary>A map entry: field 1 = key (string), field 2 = value (Feed message).</summary>
    private static UpstoxFeed? ParseMapEntry(byte[] entry)
    {
        var input = new CodedInputStream(entry);
        string key = string.Empty;
        byte[]? feedBytes = null;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (FieldOf(tag))
            {
                case 1 when WireOf(tag) == WireLengthDelimited: key = input.ReadString(); break;
                case 2 when WireOf(tag) == WireLengthDelimited: feedBytes = input.ReadBytes().ToByteArray(); break;
                default: input.SkipLastField(); break;
            }
        }
        if (string.IsNullOrEmpty(key) || feedBytes is null) return null;

        var feed = new UpstoxFeed { InstrumentKey = key };
        ParseFeed(feedBytes, feed);
        return feed;
    }

    /// <summary>Feed: 1 = LTPC (ltpc-only mode), 2 = FullFeed, 3 = FirstLevelWithGreeks.</summary>
    private static void ParseFeed(byte[] bytes, UpstoxFeed feed)
    {
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (FieldOf(tag))
            {
                case 1 when WireOf(tag) == WireLengthDelimited: ParseLtpc(input.ReadBytes().ToByteArray(), feed); break;
                case 2 when WireOf(tag) == WireLengthDelimited: ParseFullFeed(input.ReadBytes().ToByteArray(), feed); break;
                case 3 when WireOf(tag) == WireLengthDelimited: ParseFirstLevel(input.ReadBytes().ToByteArray(), feed); break;
                default: input.SkipLastField(); break;
            }
        }
    }

    /// <summary>FullFeed: 1 = MarketFullFeed, 2 = IndexFullFeed.</summary>
    private static void ParseFullFeed(byte[] bytes, UpstoxFeed feed)
    {
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (FieldOf(tag))
            {
                case 1 when WireOf(tag) == WireLengthDelimited: ParseMarketFullFeed(input.ReadBytes().ToByteArray(), feed); break;
                case 2 when WireOf(tag) == WireLengthDelimited: ParseIndexFullFeed(input.ReadBytes().ToByteArray(), feed); break;
                default: input.SkipLastField(); break;
            }
        }
    }

    /// <summary>MarketFullFeed: 1 = LTPC, 2 = MarketLevel (bid/ask book).</summary>
    private static void ParseMarketFullFeed(byte[] bytes, UpstoxFeed feed)
    {
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (FieldOf(tag))
            {
                case 1 when WireOf(tag) == WireLengthDelimited: ParseLtpc(input.ReadBytes().ToByteArray(), feed); break;
                case 2 when WireOf(tag) == WireLengthDelimited: ParseMarketLevel(input.ReadBytes().ToByteArray(), feed); break;
                default: input.SkipLastField(); break;
            }
        }
    }

    /// <summary>IndexFullFeed: 1 = LTPC (indices have no book).</summary>
    private static void ParseIndexFullFeed(byte[] bytes, UpstoxFeed feed)
    {
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (FieldOf(tag) == 1 && WireOf(tag) == WireLengthDelimited)
                ParseLtpc(input.ReadBytes().ToByteArray(), feed);
            else
                input.SkipLastField();
        }
    }

    /// <summary>FirstLevelWithGreeks: 1 = LTPC, 2 = Quote (single top-of-book level).</summary>
    private static void ParseFirstLevel(byte[] bytes, UpstoxFeed feed)
    {
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (FieldOf(tag))
            {
                case 1 when WireOf(tag) == WireLengthDelimited: ParseLtpc(input.ReadBytes().ToByteArray(), feed); break;
                case 2 when WireOf(tag) == WireLengthDelimited: feed.Levels.Add(ParseQuote(input.ReadBytes().ToByteArray())); break;
                default: input.SkipLastField(); break;
            }
        }
    }

    /// <summary>MarketLevel: 1 = repeated Quote (up to five depth levels, best first).</summary>
    private static void ParseMarketLevel(byte[] bytes, UpstoxFeed feed)
    {
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (FieldOf(tag) == 1 && WireOf(tag) == WireLengthDelimited)
                feed.Levels.Add(ParseQuote(input.ReadBytes().ToByteArray()));
            else
                input.SkipLastField();
        }
    }

    /// <summary>Quote: 1 = bidQ (int64), 2 = bidP (double), 3 = askQ (int64), 4 = askP (double).</summary>
    private static UpstoxLevel ParseQuote(byte[] bytes)
    {
        var input = new CodedInputStream(bytes);
        long bidQ = 0, askQ = 0;
        double bidP = 0, askP = 0;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (FieldOf(tag))
            {
                case 1 when WireOf(tag) == WireVarint: bidQ = input.ReadInt64(); break;
                case 2 when WireOf(tag) == WireFixed64: bidP = input.ReadDouble(); break;
                case 3 when WireOf(tag) == WireVarint: askQ = input.ReadInt64(); break;
                case 4 when WireOf(tag) == WireFixed64: askP = input.ReadDouble(); break;
                default: input.SkipLastField(); break;
            }
        }
        return new UpstoxLevel(bidP, bidQ, askP, askQ);
    }

    /// <summary>LTPC: 1 = ltp (double). The rest (ltt/ltq/cp) are not needed here.</summary>
    private static void ParseLtpc(byte[] bytes, UpstoxFeed feed)
    {
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (FieldOf(tag) == 1 && WireOf(tag) == WireFixed64)
                feed.Ltp = input.ReadDouble();
            else
                input.SkipLastField();
        }
    }

    private static int FieldOf(uint tag) => (int)(tag >> 3);
    private static int WireOf(uint tag) => (int)(tag & 0x7);
}
