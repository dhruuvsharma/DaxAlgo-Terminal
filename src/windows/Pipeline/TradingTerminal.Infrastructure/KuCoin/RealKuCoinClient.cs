using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.KuCoin;

/// <summary>
/// KuCoin spot over the public socket (no key, no account). L1 ← <c>/market/ticker</c>, L2 ←
/// <c>/spotMarket/level2Depth50</c> (a fifty-level snapshot per push), trades ← <c>/market/match</c>,
/// bars ← <c>/market/candles</c> live and <c>/api/v1/market/candles</c> for history.
///
/// <para><b>The socket URL is not fixed.</b> Each connection starts with <c>POST
/// /api/v1/bullet-public</c>, which returns a server and a token valid for that connection; a URL
/// cached from an earlier one is refused. So the URL is resolved on every connect — including every
/// reconnect — rather than once.</para>
///
/// <para><b>Candle rows are ordered open, close, high, low</b> — not the open/high/low/close every other
/// venue uses. Read positionally with the usual assumption, a KuCoin bar's high would be its close.</para>
/// </summary>
internal sealed class RealKuCoinClient : PublicCryptoClient<KuCoinOptions>
{
    public RealKuCoinClient(ILogger<RealKuCoinClient> logger, IOptions<KuCoinOptions> options)
        : base(logger, options.Value) { }

    public override BrokerKind Kind => BrokerKind.KuCoin;
    protected override string VenueName => "KuCoin";
    protected override string ExchangeCode => "KUCOIN";
    protected override string ConnectCheckPath => "/api/v1/timestamp";

    protected override string Symbol(Contract contract) => contract.Symbol.Trim().ToUpperInvariant();

    public override IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
        Json(Sub($"/market/ticker:{Symbol(contract)}"), "ticker", el => ParseTicker(el, Options.SizeScale), ct);

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default) =>
        Json(Sub($"/spotMarket/level2Depth50:{Symbol(contract)}"), "level2Depth50", el => ParseBook(el, levels, Options.SizeScale), ct);

    public override IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        Json(Sub($"/market/match:{Symbol(contract)}"), "match", el => ParseTrade(el, Options.SizeScale), ct);

    protected override bool HasRestInterval(BarSize size) => true;
    protected override bool HasLiveInterval(BarSize size) => true;

    protected override IAsyncEnumerable<Bar> StreamBarsAsync(string symbol, BarSize size, CancellationToken ct) =>
        Json(Sub($"/market/candles:{symbol}_{Interval(size)}"), "candles", el => ParseCandle(el, Options.SizeScale), ct);

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, int count, CancellationToken ct)
    {
        // Paged by time rather than count: ask for the window that holds `count` bars.
        var end = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var start = end - (long)(size.ToTimeSpan().TotalSeconds * count);
        var url = $"{Options.RestBaseUrl}/api/v1/market/candles?type={Interval(size)}&symbol={symbol}&startAt={start}&endAt={end}";
        var (doc, body) = await GetJsonAsync(url, ct).ConfigureAwait(false);
        using (doc)
            return WireFormat.OrWarn(ParseRestCandles(doc.RootElement, Options.SizeScale), body, Logger, VenueName, "candles");
    }

    private CryptoStreamSpec Sub(string topic)
    {
        var ping = $"{{\"id\":\"{Guid.NewGuid():N}\",\"type\":\"ping\"}}";
        return new CryptoStreamSpec
        {
            Name = VenueName,
            Url = ResolveSocketUrlAsync,
            Subscribe = [$"{{\"id\":\"{Guid.NewGuid():N}\",\"type\":\"subscribe\",\"topic\":\"{topic}\",\"response\":true}}"],
            // KuCoin advertises an 18 s ping interval; a socket silent for longer is closed.
            Ping = ping,
            PingIntervalSeconds = 15,
            InitialDelaySeconds = Options.ReconnectInitialDelaySeconds,
            MaxDelaySeconds = Options.ReconnectMaxDelaySeconds,
        };
    }

    /// <summary>Asks KuCoin for this connection's server and token.</summary>
    private async Task<string> ResolveSocketUrlAsync(CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(
            () => Http.PostAsync($"{Options.RestBaseUrl}/api/v1/bullet-public", content: null, ct), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return SocketUrl(body, Options.WsBaseUrl, Guid.NewGuid().ToString("N"))
            ?? throw new HttpRequestException($"KuCoin issued no socket token: {(body.Length > 200 ? body[..200] : body)}");
    }

    /// <summary>The socket URL out of a <c>bullet-public</c> answer, or null when it holds no token.</summary>
    internal static string? SocketUrl(string bulletBody, string fallbackEndpoint, string connectId)
    {
        using var doc = JsonDocument.Parse(bulletBody);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
        var token = data.TryGetProperty("token", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(token)) return null;

        var endpoint = fallbackEndpoint;
        if (data.TryGetProperty("instanceServers", out var servers) && servers.ValueKind == JsonValueKind.Array)
            foreach (var server in servers.EnumerateArray())
                if (server.TryGetProperty("endpoint", out var e) && !string.IsNullOrEmpty(e.GetString()))
                {
                    endpoint = e.GetString()!;
                    break;
                }

        return $"{endpoint}?token={Uri.EscapeDataString(token)}&connectId={connectId}";
    }

    // ── Parsers ─────────────────────────────────────────────────────────────────────────────────

    private static bool IsMessage(JsonElement el, out JsonElement data)
    {
        data = default;
        return el.TryGetProperty("type", out var type) && type.GetString() == "message"
            && el.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Object;
    }

    internal static IEnumerable<Tick> ParseTicker(JsonElement el, double scale)
    {
        if (!IsMessage(el, out var d)) yield break;
        yield return new Tick(
            CryptoConvert.MsUtc(d, "time"),
            CryptoConvert.D(d, "bestBid"), CryptoConvert.D(d, "bestAsk"),
            CryptoConvert.ToSize(CryptoConvert.D(d, "bestBidSize"), scale),
            CryptoConvert.ToSize(CryptoConvert.D(d, "bestAskSize"), scale));
    }

    internal static IEnumerable<DepthSnapshot> ParseBook(JsonElement el, int levels, double scale)
    {
        if (!IsMessage(el, out var d)) yield break;
        var book = new L2OrderBook();
        CryptoConvert.ApplyLevels(book, d, "bids", isBid: true);
        CryptoConvert.ApplyLevels(book, d, "asks", isBid: false);
        yield return book.Snapshot(levels, scale, CryptoConvert.MsUtc(d, "timestamp"));
    }

    internal static IEnumerable<TradeTick> ParseTrade(JsonElement el, double scale)
    {
        if (!IsMessage(el, out var d)) yield break;

        // `time` is nanoseconds, as a string.
        var nanos = d.TryGetProperty("time", out var time) ? CryptoConvert.L(time) : 0;
        yield return new TradeTick(
            CryptoConvert.MsUtc(nanos / 1_000_000),
            CryptoConvert.D(d, "price"),
            CryptoConvert.ToSize(CryptoConvert.D(d, "size"), scale),
            d.TryGetProperty("side", out var side) && side.GetString() == "sell" ? AggressorSide.Sell : AggressorSide.Buy);
    }

    internal static IEnumerable<Bar> ParseCandle(JsonElement el, double scale)
    {
        if (!IsMessage(el, out var d) || !d.TryGetProperty("candles", out var row)) yield break;
        if (Row(row, scale) is { } bar) yield return bar;
    }

    internal static IReadOnlyList<Bar> ParseRestCandles(JsonElement root, double scale)
    {
        var bars = new List<Bar>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var row in data.EnumerateArray())
                if (Row(row, scale) is { } bar) bars.Add(bar);
        return bars;
    }

    /// <summary>One candle row: <c>[time (s), OPEN, CLOSE, HIGH, LOW, volume, turnover]</c>.</summary>
    private static Bar? Row(JsonElement row, double scale)
    {
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6) return null;
        return new Bar(
            CryptoConvert.SecondsUtc(CryptoConvert.L(row[0])),
            Open: CryptoConvert.D(row[1]),
            High: CryptoConvert.D(row[3]),
            Low: CryptoConvert.D(row[4]),
            Close: CryptoConvert.D(row[2]),
            Volume: CryptoConvert.ToSize(CryptoConvert.D(row[5]), scale));
    }

    internal static string Interval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1min",
        BarSize.ThreeMinutes => "3min",
        BarSize.FiveMinutes => "5min",
        BarSize.FifteenMinutes => "15min",
        BarSize.OneHour => "1hour",
        BarSize.OneDay => "1day",
        _ => "1min",
    };
}
