using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Htx;

/// <summary>
/// HTX (formerly Huobi) spot over the public socket (no key, no account). L1 ← <c>market.*.bbo</c>, L2 ←
/// <c>market.*.mbp.refresh.20</c> (a twenty-level snapshot per push), trades ←
/// <c>market.*.trade.detail</c>, bars ← <c>market.*.kline.*</c> live and <c>/market/history/kline</c>
/// for history.
///
/// <para><b>Every frame is gzipped</b>, text included, and <b>the server pings the client</b>:
/// <c>{"ping":n}</c> must be answered <c>{"pong":n}</c> within a few seconds or the socket is dropped.
/// Symbols are lower-case (<c>btcusdt</c>). Kline <c>amount</c> is the base volume; <c>vol</c> is quote
/// turnover. Three-minute klines work although the documentation lists none (verified live).</para>
/// </summary>
internal sealed class RealHtxClient : PublicCryptoClient<HtxOptions>
{
    public RealHtxClient(ILogger<RealHtxClient> logger, IOptions<HtxOptions> options)
        : base(logger, options.Value) { }

    public override BrokerKind Kind => BrokerKind.Htx;
    protected override string VenueName => "HTX";
    protected override string ExchangeCode => "HTX";
    protected override string ConnectCheckPath => "/v1/common/timestamp";

    protected override string Symbol(Contract contract) => contract.Symbol.Trim().ToLowerInvariant();

    public override IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
        Stream($"market.{Symbol(contract)}.bbo", el => ParseBbo(el, Options.SizeScale), ct);

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default) =>
        Stream($"market.{Symbol(contract)}.mbp.refresh.{(Options.DepthLevels <= 5 ? 5 : Options.DepthLevels <= 10 ? 10 : 20)}",
            el => ParseBook(el, levels, Options.SizeScale), ct);

    public override IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        Stream($"market.{Symbol(contract)}.trade.detail", el => ParseTrades(el, Options.SizeScale), ct);

    protected override bool HasRestInterval(BarSize size) => true;
    protected override bool HasLiveInterval(BarSize size) => true;

    protected override IAsyncEnumerable<Bar> StreamBarsAsync(string symbol, BarSize size, CancellationToken ct) =>
        Stream($"market.{symbol}.kline.{Interval(size)}", el => ParseKline(el, Options.SizeScale), ct);

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, int count, CancellationToken ct)
    {
        var url = $"{Options.RestBaseUrl}/market/history/kline?symbol={symbol}&period={Interval(size)}&size={Math.Min(count, 2000)}";
        var (doc, body) = await GetJsonAsync(url, ct).ConfigureAwait(false);
        using (doc)
        {
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;
            return WireFormat.OrWarn(ParseRows(data, Options.SizeScale), body, Logger, VenueName, "kline");
        }
    }

    private IAsyncEnumerable<T> Stream<T>(string topic, Func<JsonElement, IEnumerable<T>> parse, CancellationToken ct)
    {
        var spec = new CryptoStreamSpec
        {
            Name = VenueName,
            Url = CryptoStreamSpec.Fixed(Options.WsBaseUrl),
            Subscribe = [$"{{\"sub\":\"{topic}\",\"id\":\"{Guid.NewGuid():N}\"}}"],
            Decode = Gunzip,
            InitialDelaySeconds = Options.ReconnectInitialDelaySeconds,
            MaxDelaySeconds = Options.ReconnectMaxDelaySeconds,
        };
        return Json(spec, topic, parse, ct, PongFor);
    }

    internal static byte[] Gunzip(byte[] frame)
    {
        using var input = new GZipStream(new MemoryStream(frame), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>The answer to a server ping, or null for any other frame.</summary>
    internal static string? PongFor(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty("ping", out var ping)
            ? $"{{\"pong\":{ping.GetRawText()}}}"
            : null;

    // ── Parsers ─────────────────────────────────────────────────────────────────────────────────

    private static bool Payload(JsonElement el, out JsonElement tick)
    {
        tick = default;
        return el.TryGetProperty("ch", out _) && el.TryGetProperty("tick", out tick) && tick.ValueKind == JsonValueKind.Object;
    }

    internal static IEnumerable<Tick> ParseBbo(JsonElement el, double scale)
    {
        if (!Payload(el, out var t)) yield break;
        yield return new Tick(
            CryptoConvert.MsUtc(t, "quoteTime"),
            CryptoConvert.D(t, "bid"), CryptoConvert.D(t, "ask"),
            CryptoConvert.ToSize(CryptoConvert.D(t, "bidSize"), scale),
            CryptoConvert.ToSize(CryptoConvert.D(t, "askSize"), scale));
    }

    internal static IEnumerable<DepthSnapshot> ParseBook(JsonElement el, int levels, double scale)
    {
        if (!Payload(el, out var t)) yield break;
        var book = new L2OrderBook();
        CryptoConvert.ApplyLevels(book, t, "bids", isBid: true);
        CryptoConvert.ApplyLevels(book, t, "asks", isBid: false);
        yield return book.Snapshot(levels, scale, CryptoConvert.MsUtc(el, "ts"));
    }

    internal static IEnumerable<TradeTick> ParseTrades(JsonElement el, double scale)
    {
        if (!Payload(el, out var t) || !t.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) yield break;
        foreach (var d in data.EnumerateArray().OrderBy(d => CryptoConvert.MsToTicksUtc(d, "ts")))
            yield return new TradeTick(
                CryptoConvert.MsUtc(d, "ts"),
                CryptoConvert.D(d, "price"),
                CryptoConvert.ToSize(CryptoConvert.D(d, "amount"), scale),
                d.TryGetProperty("direction", out var side) && side.GetString() == "sell" ? AggressorSide.Sell : AggressorSide.Buy);
    }

    internal static IEnumerable<Bar> ParseKline(JsonElement el, double scale)
    {
        if (!Payload(el, out var t)) yield break;
        if (Row(t, scale) is { } bar) yield return bar;
    }

    internal static IReadOnlyList<Bar> ParseRows(JsonElement rows, double scale)
    {
        var bars = new List<Bar>();
        if (rows.ValueKind != JsonValueKind.Array) return bars;
        foreach (var row in rows.EnumerateArray())
            if (Row(row, scale) is { } bar) bars.Add(bar);
        return bars;
    }

    /// <summary><c>{id (s), open, close, low, high, amount (base), vol (quote), count}</c>.</summary>
    private static Bar? Row(JsonElement row, double scale)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("id", out var id)) return null;
        return new Bar(
            CryptoConvert.SecondsUtc(CryptoConvert.L(id)),
            CryptoConvert.D(row, "open"), CryptoConvert.D(row, "high"), CryptoConvert.D(row, "low"), CryptoConvert.D(row, "close"),
            CryptoConvert.ToSize(CryptoConvert.D(row, "amount"), scale));
    }

    internal static string Interval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1min",
        BarSize.ThreeMinutes => "3min",
        BarSize.FiveMinutes => "5min",
        BarSize.FifteenMinutes => "15min",
        BarSize.OneHour => "60min",
        BarSize.OneDay => "1day",
        _ => "1min",
    };
}
