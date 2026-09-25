using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Mexc;

/// <summary>
/// MEXC spot over the public socket (no key, no account). L1 ← <c>aggre.bookTicker</c>, L2 ←
/// <c>limit.depth</c> (a snapshot per push), trades ← <c>aggre.deals</c>, bars ← <c>kline</c> live and
/// <c>/api/v3/klines</c> (Binance-shaped) for history.
///
/// <para><b>Protocol buffers only.</b> A JSON subscription on the public socket is answered
/// "Blocked!"; the current endpoint accepts <c>…v3.api.pb</c> channels and pushes binary protobuf. Acks and
/// pongs still come back as JSON text, so a frame starting with <c>{</c> is control, anything else data.</para>
///
/// <para>The wrapper's field numbers were read off live frames on 2026-09-25 and match MEXC's published
/// <c>PushDataV3ApiWrapper</c>: channel 1, symbol 3, send time 6, limit depths 303, kline 308,
/// aggregated deals 314, aggregated book ticker 315. Deal <c>tradeType</c> is 1 for a buy, 2 for a sell;
/// depth field 1 is asks, 2 bids; kline fields run interval, window start (s), open, CLOSE, HIGH, LOW,
/// volume.</para>
///
/// <para>No three-minute kline is published; those are rolled up from one-minute bars.</para>
/// </summary>
internal sealed class RealMexcClient : PublicCryptoClient<MexcOptions>
{
    public RealMexcClient(ILogger<RealMexcClient> logger, IOptions<MexcOptions> options)
        : base(logger, options.Value) { }

    public override BrokerKind Kind => BrokerKind.Mexc;
    protected override string VenueName => "MEXC";
    protected override string ExchangeCode => "MEXC";
    protected override string ConnectCheckPath => "/api/v3/time";

    protected override string Symbol(Contract contract) => contract.Symbol.Trim().ToUpperInvariant();

    public override IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
        Stream($"spot@public.aggre.bookTicker.v3.api.pb@100ms@{Symbol(contract)}", frame => ParseBookTicker(frame, Options.SizeScale), ct);

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default)
    {
        var depth = Options.DepthLevels <= 5 ? 5 : Options.DepthLevels <= 10 ? 10 : 20;
        return Stream($"spot@public.limit.depth.v3.api.pb@{Symbol(contract)}@{depth}", frame => ParseDepth(frame, levels, Options.SizeScale), ct);
    }

    public override IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        Stream($"spot@public.aggre.deals.v3.api.pb@100ms@{Symbol(contract)}", frame => ParseDeals(frame, Options.SizeScale), ct);

    protected override bool HasRestInterval(BarSize size) => size != BarSize.ThreeMinutes;
    protected override bool HasLiveInterval(BarSize size) => size != BarSize.ThreeMinutes;

    protected override IAsyncEnumerable<Bar> StreamBarsAsync(string symbol, BarSize size, CancellationToken ct) =>
        Stream($"spot@public.kline.v3.api.pb@{symbol}@{LiveInterval(size)}", frame => ParseKline(frame, Options.SizeScale), ct);

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, int count, CancellationToken ct)
    {
        var url = $"{Options.RestBaseUrl}/api/v3/klines?symbol={symbol}&interval={RestInterval(size)}&limit={Math.Min(count, 1000)}";
        var (doc, body) = await GetJsonAsync(url, ct).ConfigureAwait(false);
        using (doc)
            return WireFormat.OrWarn(ParseRestKlines(doc.RootElement, Options.SizeScale), body, Logger, VenueName, "klines");
    }

    private IAsyncEnumerable<T> Stream<T>(string channel, Func<byte[], IReadOnlyList<T>> parse, CancellationToken ct)
    {
        var spec = new CryptoStreamSpec
        {
            Name = VenueName,
            Url = CryptoStreamSpec.Fixed(Options.WsBaseUrl),
            Subscribe = [$"{{\"method\":\"SUBSCRIPTION\",\"params\":[\"{channel}\"]}}"],
            Ping = "{\"method\":\"PING\"}",
            PingIntervalSeconds = 20,
            InitialDelaySeconds = Options.ReconnectInitialDelaySeconds,
            MaxDelaySeconds = Options.ReconnectMaxDelaySeconds,
        };

        var watch = new WireFormat.StreamWatch(Logger, VenueName, channel);
        return CryptoStream.StreamFramesAsync(spec, frame =>
        {
            if (frame.Length == 0 || frame[0] == (byte)'{') return CryptoFrame<T>.None;   // ack or pong
            var items = parse(frame);
            watch.Observe(items.Count);
            return new CryptoFrame<T>(items);
        }, Logger, ct);
    }

    // ── Protobuf ────────────────────────────────────────────────────────────────────────────────

    private const int FieldLimitDepths = 303;
    private const int FieldKline = 308;
    private const int FieldAggreDeals = 314;
    private const int FieldAggreBookTicker = 315;

    /// <summary>The payload of wrapper field <paramref name="field"/>, and the wrapper's send time.</summary>
    private static bool Body(ReadOnlySpan<byte> frame, int field, out ReadOnlySpan<byte> body, out DateTime sent)
    {
        body = default;
        sent = DateTime.UtcNow;
        var found = false;
        var reader = new ProtoReader(frame);
        while (reader.Next(out var f, out var wire))
        {
            if (f == field && wire == ProtoReader.Len)
            {
                body = reader.ReadBytes();
                found = true;
            }
            else if (f == 6 && wire == ProtoReader.Varint)
            {
                sent = CryptoConvert.MsUtc((long)reader.ReadVarint());
            }
            else
            {
                reader.Skip(wire);
            }
        }

        return found;
    }

    private static double Num(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    internal static IReadOnlyList<Tick> ParseBookTicker(byte[] frame, double scale)
    {
        if (!Body(frame, FieldAggreBookTicker, out var body, out var sent)) return [];
        double bid = 0, bidQty = 0, ask = 0, askQty = 0;
        var reader = new ProtoReader(body);
        while (reader.Next(out var f, out var wire))
        {
            switch (f)
            {
                case 1 when wire == ProtoReader.Len: bid = Num(reader.ReadString()); break;
                case 2 when wire == ProtoReader.Len: bidQty = Num(reader.ReadString()); break;
                case 3 when wire == ProtoReader.Len: ask = Num(reader.ReadString()); break;
                case 4 when wire == ProtoReader.Len: askQty = Num(reader.ReadString()); break;
                default: reader.Skip(wire); break;
            }
        }

        return bid > 0 && ask > 0
            ? [new Tick(sent, bid, ask, CryptoConvert.ToSize(bidQty, scale), CryptoConvert.ToSize(askQty, scale))]
            : [];
    }

    internal static IReadOnlyList<DepthSnapshot> ParseDepth(byte[] frame, int levels, double scale)
    {
        if (!Body(frame, FieldLimitDepths, out var body, out var sent)) return [];
        var book = new L2OrderBook();
        var reader = new ProtoReader(body);
        while (reader.Next(out var f, out var wire))
        {
            if (f is 1 or 2 && wire == ProtoReader.Len)
            {
                var (price, qty) = Level(reader.ReadBytes());
                book.Apply(isBid: f == 2, price, qty);
            }
            else
            {
                reader.Skip(wire);
            }
        }

        return book.IsEmpty ? [] : [book.Snapshot(levels, scale, sent)];
    }

    private static (double Price, double Quantity) Level(ReadOnlySpan<byte> item)
    {
        double price = 0, qty = 0;
        var reader = new ProtoReader(item);
        while (reader.Next(out var f, out var wire))
        {
            if (f == 1 && wire == ProtoReader.Len) price = Num(reader.ReadString());
            else if (f == 2 && wire == ProtoReader.Len) qty = Num(reader.ReadString());
            else reader.Skip(wire);
        }

        return (price, qty);
    }

    internal static IReadOnlyList<TradeTick> ParseDeals(byte[] frame, double scale)
    {
        if (!Body(frame, FieldAggreDeals, out var body, out _)) return [];
        var trades = new List<TradeTick>();
        var reader = new ProtoReader(body);
        while (reader.Next(out var f, out var wire))
        {
            if (f != 1 || wire != ProtoReader.Len)
            {
                reader.Skip(wire);
                continue;
            }

            double price = 0, qty = 0;
            long type = 0, time = 0;
            var deal = new ProtoReader(reader.ReadBytes());
            while (deal.Next(out var df, out var dw))
            {
                switch (df)
                {
                    case 1 when dw == ProtoReader.Len: price = Num(deal.ReadString()); break;
                    case 2 when dw == ProtoReader.Len: qty = Num(deal.ReadString()); break;
                    case 3 when dw == ProtoReader.Varint: type = (long)deal.ReadVarint(); break;
                    case 4 when dw == ProtoReader.Varint: time = (long)deal.ReadVarint(); break;
                    default: deal.Skip(dw); break;
                }
            }

            trades.Add(new TradeTick(CryptoConvert.MsUtc(time), price, CryptoConvert.ToSize(qty, scale),
                type == 2 ? AggressorSide.Sell : AggressorSide.Buy));
        }

        return trades.OrderBy(t => t.TimestampUtc).ToList();
    }

    internal static IReadOnlyList<Bar> ParseKline(byte[] frame, double scale)
    {
        if (!Body(frame, FieldKline, out var body, out _)) return [];
        long start = 0;
        double open = 0, close = 0, high = 0, low = 0, volume = 0;
        var reader = new ProtoReader(body);
        while (reader.Next(out var f, out var wire))
        {
            switch (f)
            {
                case 2 when wire == ProtoReader.Varint: start = (long)reader.ReadVarint(); break;
                case 3 when wire == ProtoReader.Len: open = Num(reader.ReadString()); break;
                case 4 when wire == ProtoReader.Len: close = Num(reader.ReadString()); break;
                case 5 when wire == ProtoReader.Len: high = Num(reader.ReadString()); break;
                case 6 when wire == ProtoReader.Len: low = Num(reader.ReadString()); break;
                case 7 when wire == ProtoReader.Len: volume = Num(reader.ReadString()); break;
                default: reader.Skip(wire); break;
            }
        }

        return start > 0
            ? [new Bar(CryptoConvert.SecondsUtc(start), open, high, low, close, CryptoConvert.ToSize(volume, scale))]
            : [];
    }

    /// <summary>Binance-shaped rows: <c>[openTime (ms), open, high, low, close, volume, closeTime, quoteVolume]</c>.</summary>
    internal static IReadOnlyList<Bar> ParseRestKlines(JsonElement root, double scale)
    {
        var bars = new List<Bar>();
        if (root.ValueKind != JsonValueKind.Array) return bars;
        foreach (var row in root.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 6)
                bars.Add(new Bar(
                    CryptoConvert.MsUtc(CryptoConvert.L(row[0])),
                    CryptoConvert.D(row[1]), CryptoConvert.D(row[2]), CryptoConvert.D(row[3]), CryptoConvert.D(row[4]),
                    CryptoConvert.ToSize(CryptoConvert.D(row[5]), scale)));
        return bars;
    }

    internal static string LiveInterval(BarSize size) => size switch
    {
        BarSize.OneMinute => "Min1",
        BarSize.FiveMinutes => "Min5",
        BarSize.FifteenMinutes => "Min15",
        BarSize.OneHour => "Min60",
        BarSize.OneDay => "Day1",
        _ => "Min1",
    };

    internal static string RestInterval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1m",
        BarSize.FiveMinutes => "5m",
        BarSize.FifteenMinutes => "15m",
        BarSize.OneHour => "60m",
        BarSize.OneDay => "1d",
        _ => "1m",
    };
}
