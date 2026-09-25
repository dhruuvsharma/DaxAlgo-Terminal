using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.GateIo;

/// <summary>
/// Gate.io spot over the public v4 socket (no key, no account). L1 ← <c>spot.book_ticker</c>, L2 ←
/// <c>spot.order_book</c> (a twenty-level snapshot every 100 ms), trades ← <c>spot.trades</c>, bars ←
/// <c>spot.candlesticks</c> live and <c>/api/v4/spot/candlesticks</c> for history.
///
/// <para><b>The two candle shapes disagree.</b> The socket sends named fields, and its <c>a</c> is the
/// base-currency amount while <c>v</c> is quote turnover. REST sends a positional row ordered
/// <c>[t, quoteVolume, close, high, low, OPEN, baseVolume, closed]</c> — open second to last. Both are
/// read by name or by explicit index here, never by assumed order.</para>
///
/// <para>Three-minute candles work on both paths although the documentation lists no such interval;
/// verified against the live venue on 2026-09-25.</para>
/// </summary>
internal sealed class RealGateIoClient : PublicCryptoClient<GateIoOptions>
{
    public RealGateIoClient(ILogger<RealGateIoClient> logger, IOptions<GateIoOptions> options)
        : base(logger, options.Value) { }

    public override BrokerKind Kind => BrokerKind.GateIo;
    protected override string VenueName => "Gate.io";
    protected override string ExchangeCode => "GATEIO";
    protected override string ConnectCheckPath => "/api/v4/spot/time";

    protected override string Symbol(Contract contract) => contract.Symbol.Trim().ToUpperInvariant();

    public override IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
        Json(Sub("spot.book_ticker", $"\"{Symbol(contract)}\""), "spot.book_ticker", el => ParseBookTicker(el, Options.SizeScale), ct);

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default) =>
        Json(Sub("spot.order_book", $"\"{Symbol(contract)}\",\"{Depth()}\",\"100ms\""), "spot.order_book",
            el => ParseBook(el, levels, Options.SizeScale), ct);

    public override IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        Json(Sub("spot.trades", $"\"{Symbol(contract)}\""), "spot.trades", el => ParseTrade(el, Options.SizeScale), ct);

    protected override bool HasRestInterval(BarSize size) => true;
    protected override bool HasLiveInterval(BarSize size) => true;

    protected override IAsyncEnumerable<Bar> StreamBarsAsync(string symbol, BarSize size, CancellationToken ct) =>
        Json(Sub("spot.candlesticks", $"\"{Interval(size)}\",\"{symbol}\""), "spot.candlesticks", el => ParseCandle(el, Options.SizeScale), ct);

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, int count, CancellationToken ct)
    {
        var url = $"{Options.RestBaseUrl}/api/v4/spot/candlesticks?currency_pair={symbol}&interval={Interval(size)}&limit={count}";
        var (doc, body) = await GetJsonAsync(url, ct).ConfigureAwait(false);
        using (doc)
            return WireFormat.OrWarn(ParseRestCandles(doc.RootElement, Options.SizeScale), body, Logger, VenueName, "candlesticks");
    }

    /// <summary>Gate.io accepts 5, 10, 20, 50 or 100 levels.</summary>
    private int Depth() => Options.DepthLevels switch
    {
        <= 5 => 5,
        <= 10 => 10,
        <= 20 => 20,
        <= 50 => 50,
        _ => 100,
    };

    private CryptoStreamSpec Sub(string channel, string payload)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new CryptoStreamSpec
        {
            Name = VenueName,
            Url = CryptoStreamSpec.Fixed(Options.WsBaseUrl),
            Subscribe = [$"{{\"time\":{now},\"channel\":\"{channel}\",\"event\":\"subscribe\",\"payload\":[{payload}]}}"],
            Ping = $"{{\"time\":{now},\"channel\":\"spot.ping\"}}",
            PingIntervalSeconds = 20,
            InitialDelaySeconds = Options.ReconnectInitialDelaySeconds,
            MaxDelaySeconds = Options.ReconnectMaxDelaySeconds,
        };
    }

    // ── Parsers ─────────────────────────────────────────────────────────────────────────────────

    private static bool IsUpdate(JsonElement el, out JsonElement result)
    {
        result = default;
        return el.TryGetProperty("event", out var ev) && ev.GetString() == "update"
            && el.TryGetProperty("result", out result) && result.ValueKind == JsonValueKind.Object;
    }

    internal static IEnumerable<Tick> ParseBookTicker(JsonElement el, double scale)
    {
        if (!IsUpdate(el, out var r)) yield break;
        yield return new Tick(
            CryptoConvert.MsUtc(r, "t"),
            CryptoConvert.D(r, "b"), CryptoConvert.D(r, "a"),
            CryptoConvert.ToSize(CryptoConvert.D(r, "B"), scale),
            CryptoConvert.ToSize(CryptoConvert.D(r, "A"), scale));
    }

    internal static IEnumerable<DepthSnapshot> ParseBook(JsonElement el, int levels, double scale)
    {
        if (!IsUpdate(el, out var r)) yield break;
        var book = new L2OrderBook();
        CryptoConvert.ApplyLevels(book, r, "bids", isBid: true);
        CryptoConvert.ApplyLevels(book, r, "asks", isBid: false);
        yield return book.Snapshot(levels, scale, CryptoConvert.MsUtc(r, "t"));
    }

    internal static IEnumerable<TradeTick> ParseTrade(JsonElement el, double scale)
    {
        if (!IsUpdate(el, out var r)) yield break;
        yield return new TradeTick(
            // create_time_ms is a string with a fractional part: "1790274249452.965000".
            CryptoConvert.MsUtc(r, "create_time_ms"),
            CryptoConvert.D(r, "price"),
            CryptoConvert.ToSize(CryptoConvert.D(r, "amount"), scale),
            r.TryGetProperty("side", out var side) && side.GetString() == "sell" ? AggressorSide.Sell : AggressorSide.Buy);
    }

    internal static IEnumerable<Bar> ParseCandle(JsonElement el, double scale)
    {
        if (!IsUpdate(el, out var r)) yield break;
        yield return new Bar(
            CryptoConvert.SecondsUtc(r.TryGetProperty("t", out var t) ? CryptoConvert.L(t) : 0),
            CryptoConvert.D(r, "o"), CryptoConvert.D(r, "h"), CryptoConvert.D(r, "l"), CryptoConvert.D(r, "c"),
            // `a` is the base amount; `v` is quote turnover.
            CryptoConvert.ToSize(CryptoConvert.D(r, "a"), scale));
    }

    /// <summary>REST rows: <c>[t, quoteVolume, close, high, low, open, baseVolume, closed]</c>.</summary>
    internal static IReadOnlyList<Bar> ParseRestCandles(JsonElement root, double scale)
    {
        var bars = new List<Bar>();
        if (root.ValueKind != JsonValueKind.Array) return bars;
        foreach (var row in root.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 7) continue;
            bars.Add(new Bar(
                CryptoConvert.SecondsUtc(CryptoConvert.L(row[0])),
                Open: CryptoConvert.D(row[5]),
                High: CryptoConvert.D(row[3]),
                Low: CryptoConvert.D(row[4]),
                Close: CryptoConvert.D(row[2]),
                Volume: CryptoConvert.ToSize(CryptoConvert.D(row[6]), scale)));
        }

        return bars;
    }

    internal static string Interval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1m",
        BarSize.ThreeMinutes => "3m",
        BarSize.FiveMinutes => "5m",
        BarSize.FifteenMinutes => "15m",
        BarSize.OneHour => "1h",
        BarSize.OneDay => "1d",
        _ => "1m",
    };
}
