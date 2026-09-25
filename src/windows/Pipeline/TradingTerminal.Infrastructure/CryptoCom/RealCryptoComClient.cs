using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.CryptoCom;

/// <summary>
/// Crypto.com Exchange spot over the public v1 market socket (no key, no account). L1 ←
/// <c>ticker.*</c>, L2 ← <c>book.*.{10|50}</c>, trades ← <c>trade.*</c>, bars ← <c>candlestick.*</c>
/// live and <c>public/get-candlestick</c> for history.
///
/// <para><b>The heartbeat is a question.</b> The server sends <c>public/heartbeat</c> with an id and closes
/// the socket unless that id comes back in <c>public/respond-heartbeat</c> — a client-side ping does not
/// count. It is answered as it arrives.</para>
///
/// <para><b>The first message of a subscription is history.</b> The subscribe response (the one carrying
/// the request's own id) holds recent trades or a run of past candles; live pushes carry id -1. Trades
/// skip the response; candles keep only its newest row.</para>
///
/// <para>No three-minute candle is published — the venue rejects it as "Invalid timeframe" — so those are
/// rolled up from one-minute bars.</para>
/// </summary>
internal sealed class RealCryptoComClient : PublicCryptoClient<CryptoComOptions>
{
    public RealCryptoComClient(ILogger<RealCryptoComClient> logger, IOptions<CryptoComOptions> options)
        : base(logger, options.Value) { }

    public override BrokerKind Kind => BrokerKind.CryptoCom;
    protected override string VenueName => "Crypto.com";
    protected override string ExchangeCode => "CRYPTOCOM";
    protected override string ConnectCheckPath => "/public/get-tickers?instrument_name=BTC_USD";

    protected override string Symbol(Contract contract) => contract.Symbol.Trim().ToUpperInvariant();

    public override IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
        Stream($"ticker.{Symbol(contract)}", el => ParseTicker(el, Options.SizeScale), ct);

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default)
    {
        var book = new L2OrderBook();
        var depth = Options.DepthLevels <= 10 ? 10 : 50;
        return Stream($"book.{Symbol(contract)}.{depth}", el => ParseBook(el, book, levels, Options.SizeScale), ct);
    }

    public override IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        Stream($"trade.{Symbol(contract)}", el => ParseTrades(el, Options.SizeScale), ct);

    protected override bool HasRestInterval(BarSize size) => size != BarSize.ThreeMinutes;
    protected override bool HasLiveInterval(BarSize size) => size != BarSize.ThreeMinutes;

    protected override IAsyncEnumerable<Bar> StreamBarsAsync(string symbol, BarSize size, CancellationToken ct) =>
        Stream($"candlestick.{Interval(size)}.{symbol}", el => ParseCandles(el, Options.SizeScale), ct);

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, int count, CancellationToken ct)
    {
        // The venue's page limit is 300.
        var url = $"{Options.RestBaseUrl}/public/get-candlestick?instrument_name={symbol}&timeframe={Interval(size)}&count={Math.Min(count, 300)}";
        var (doc, body) = await GetJsonAsync(url, ct).ConfigureAwait(false);
        using (doc)
        {
            var data = doc.RootElement.TryGetProperty("result", out var result) && result.TryGetProperty("data", out var d)
                ? d : default;
            return WireFormat.OrWarn(ParseRows(data, Options.SizeScale), body, Logger, VenueName, "candlestick");
        }
    }

    private IAsyncEnumerable<T> Stream<T>(string channel, Func<JsonElement, IEnumerable<T>> parse, CancellationToken ct)
    {
        var spec = new CryptoStreamSpec
        {
            Name = VenueName,
            Url = CryptoStreamSpec.Fixed(Options.WsBaseUrl),
            Subscribe = [$"{{\"id\":1,\"method\":\"subscribe\",\"params\":{{\"channels\":[\"{channel}\"]}},\"nonce\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}"],
            InitialDelaySeconds = Options.ReconnectInitialDelaySeconds,
            MaxDelaySeconds = Options.ReconnectMaxDelaySeconds,
        };
        return Json(spec, channel, parse, ct, HeartbeatReply);
    }

    /// <summary>The answer to a server heartbeat, or null for any other frame.</summary>
    internal static string? HeartbeatReply(JsonElement el) =>
        el.TryGetProperty("method", out var method) && method.GetString() == "public/heartbeat"
        && el.TryGetProperty("id", out var id)
            ? $"{{\"id\":{id.GetRawText()},\"method\":\"public/respond-heartbeat\"}}"
            : null;

    // ── Parsers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The <c>result</c> of a data push, and whether it is live (id -1) or the subscribe
    /// response's history.</summary>
    private static bool Result(JsonElement el, out JsonElement result, out bool live)
    {
        result = default;
        live = el.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt64() == -1;
        return el.TryGetProperty("result", out result) && result.ValueKind == JsonValueKind.Object;
    }

    private static IEnumerable<JsonElement> Data(JsonElement result) =>
        result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray()
            : [];

    internal static IEnumerable<Tick> ParseTicker(JsonElement el, double scale)
    {
        if (!Result(el, out var r, out _)) yield break;
        foreach (var d in Data(r))
            yield return new Tick(
                CryptoConvert.MsUtc(d, "t"),
                CryptoConvert.D(d, "b"), CryptoConvert.D(d, "k"),
                CryptoConvert.ToSize(CryptoConvert.D(d, "bs"), scale),
                CryptoConvert.ToSize(CryptoConvert.D(d, "ks"), scale));
    }

    internal static IEnumerable<DepthSnapshot> ParseBook(JsonElement el, L2OrderBook book, int levels, double scale)
    {
        if (!Result(el, out var r, out _)) yield break;
        var channel = r.TryGetProperty("channel", out var c) ? c.GetString() : null;

        foreach (var d in Data(r))
        {
            if (channel == "book.update")
            {
                // Delta mode: the changes sit under `update`, and size 0 removes a level.
                if (!d.TryGetProperty("update", out var update)) continue;
                CryptoConvert.ApplyLevels(book, update, "bids", isBid: true);
                CryptoConvert.ApplyLevels(book, update, "asks", isBid: false);
            }
            else
            {
                book.Clear();
                CryptoConvert.ApplyLevels(book, d, "bids", isBid: true);
                CryptoConvert.ApplyLevels(book, d, "asks", isBid: false);
            }

            yield return book.Snapshot(levels, scale, CryptoConvert.MsUtc(d, "t"));
        }
    }

    internal static IEnumerable<TradeTick> ParseTrades(JsonElement el, double scale)
    {
        if (!Result(el, out var r, out var live) || !live) yield break;

        // Newest first within a push; reversed so the tape runs forward.
        foreach (var t in Data(r).Reverse())
            yield return new TradeTick(
                CryptoConvert.MsUtc(t, "t"),
                CryptoConvert.D(t, "p"),
                CryptoConvert.ToSize(CryptoConvert.D(t, "q"), scale),
                t.TryGetProperty("s", out var side) && side.GetString() == "SELL" ? AggressorSide.Sell : AggressorSide.Buy);
    }

    internal static IReadOnlyList<Bar> ParseCandles(JsonElement el, double scale)
    {
        if (!Result(el, out var r, out var live) || !r.TryGetProperty("data", out var data)) return [];
        var rows = ParseRows(data, scale).OrderBy(b => b.TimestampUtc).ToList();
        return live || rows.Count == 0 ? rows : [rows[^1]];
    }

    /// <summary><c>[{o, h, l, c, v, t (ms)}, …]</c>.</summary>
    internal static IReadOnlyList<Bar> ParseRows(JsonElement rows, double scale)
    {
        var bars = new List<Bar>();
        if (rows.ValueKind != JsonValueKind.Array) return bars;
        foreach (var row in rows.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Object)
                bars.Add(new Bar(
                    CryptoConvert.MsUtc(row, "t"),
                    CryptoConvert.D(row, "o"), CryptoConvert.D(row, "h"), CryptoConvert.D(row, "l"), CryptoConvert.D(row, "c"),
                    CryptoConvert.ToSize(CryptoConvert.D(row, "v"), scale)));
        return bars;
    }

    internal static string Interval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1m",
        BarSize.FiveMinutes => "5m",
        BarSize.FifteenMinutes => "15m",
        BarSize.OneHour => "1h",
        BarSize.OneDay => "1D",
        _ => "1m",
    };
}
