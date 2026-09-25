using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Bitget;

/// <summary>
/// Bitget spot over the public v2 socket (no key, no account). L1 ← <c>ticker</c>, L2 ← <c>books15</c>
/// (a full fifteen-level snapshot per push), trades ← <c>trade</c>, bars ← <c>candle*</c> live and
/// <c>/api/v2/spot/market/candles</c> for history. Keepalive is the bare word <c>ping</c>.
///
/// <para>The trade and candle channels open with a <b>history</b> snapshot (<c>action:"snapshot"</c>) —
/// the last few hundred prints or candles. Those are not live events: re-emitting them into the tape
/// would duplicate prints already stored, so trades skip the snapshot and candles keep only its newest
/// row, the one still forming.</para>
/// </summary>
internal sealed class RealBitgetClient : PublicCryptoClient<BitgetOptions>
{
    public RealBitgetClient(ILogger<RealBitgetClient> logger, IOptions<BitgetOptions> options)
        : base(logger, options.Value) { }

    public override BrokerKind Kind => BrokerKind.Bitget;
    protected override string VenueName => "Bitget";
    protected override string ExchangeCode => "BITGET";
    protected override string ConnectCheckPath => "/api/v2/public/time";

    protected override string Symbol(Contract contract) => contract.Symbol.Trim().ToUpperInvariant();

    public override IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
        Json(Sub("ticker", Symbol(contract)), "ticker", el => ParseTicker(el, Options.SizeScale), ct);

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default) =>
        Json(Sub("books15", Symbol(contract)), "books15", el => ParseBook(el, levels, Options.SizeScale), ct);

    public override IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        Json(Sub("trade", Symbol(contract)), "trade", el => ParseTrades(el, Options.SizeScale), ct);

    protected override bool HasRestInterval(BarSize size) => true;
    protected override bool HasLiveInterval(BarSize size) => true;

    protected override IAsyncEnumerable<Bar> StreamBarsAsync(string symbol, BarSize size, CancellationToken ct) =>
        Json(Sub("candle" + LiveInterval(size), symbol), "candle", el => ParseCandles(el, Options.SizeScale), ct);

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, int count, CancellationToken ct)
    {
        var url = $"{Options.RestBaseUrl}/api/v2/spot/market/candles?symbol={symbol}&granularity={RestInterval(size)}&limit={count}";
        var (doc, body) = await GetJsonAsync(url, ct).ConfigureAwait(false);
        using (doc)
            return WireFormat.OrWarn(ParseRestCandles(doc.RootElement, Options.SizeScale), body, Logger, VenueName, "candles");
    }

    private CryptoStreamSpec Sub(string channel, string symbol) =>
        new()
        {
            Name = VenueName,
            Url = CryptoStreamSpec.Fixed(Options.WsBaseUrl),
            Subscribe = [$"{{\"op\":\"subscribe\",\"args\":[{{\"instType\":\"SPOT\",\"channel\":\"{channel}\",\"instId\":\"{symbol}\"}}]}}"],
            Ping = "ping",
            PingIntervalSeconds = 25,
            InitialDelaySeconds = Options.ReconnectInitialDelaySeconds,
            MaxDelaySeconds = Options.ReconnectMaxDelaySeconds,
        };

    // ── Parsers ─────────────────────────────────────────────────────────────────────────────────

    private static bool IsSnapshot(JsonElement el) =>
        el.TryGetProperty("action", out var action) && action.GetString() == "snapshot";

    internal static IEnumerable<Tick> ParseTicker(JsonElement el, double scale)
    {
        if (!el.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) yield break;
        foreach (var d in data.EnumerateArray())
            yield return new Tick(
                CryptoConvert.MsUtc(d, "ts"),
                CryptoConvert.D(d, "bidPr"), CryptoConvert.D(d, "askPr"),
                CryptoConvert.ToSize(CryptoConvert.D(d, "bidSz"), scale),
                CryptoConvert.ToSize(CryptoConvert.D(d, "askSz"), scale));
    }

    internal static IEnumerable<DepthSnapshot> ParseBook(JsonElement el, int levels, double scale)
    {
        if (!el.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) yield break;
        foreach (var d in data.EnumerateArray())
        {
            // books15 pushes the whole top of book every time, so each message is its own snapshot.
            var book = new L2OrderBook();
            CryptoConvert.ApplyLevels(book, d, "bids", isBid: true);
            CryptoConvert.ApplyLevels(book, d, "asks", isBid: false);
            yield return book.Snapshot(levels, scale, CryptoConvert.MsUtc(d, "ts"));
        }
    }

    internal static IEnumerable<TradeTick> ParseTrades(JsonElement el, double scale)
    {
        if (IsSnapshot(el)) yield break;
        if (!el.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) yield break;

        // Newest first within a message; reversed so the tape runs forward in time.
        foreach (var t in data.EnumerateArray().Reverse())
            yield return new TradeTick(
                CryptoConvert.MsUtc(t, "ts"), CryptoConvert.D(t, "price"),
                CryptoConvert.ToSize(CryptoConvert.D(t, "size"), scale),
                t.TryGetProperty("side", out var side) && side.GetString() == "sell" ? AggressorSide.Sell : AggressorSide.Buy);
    }

    internal static IEnumerable<Bar> ParseCandles(JsonElement el, double scale)
    {
        if (!el.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) yield break;
        var rows = data.EnumerateArray().ToList();
        if (IsSnapshot(el) && rows.Count > 0) rows = [rows[^1]];
        foreach (var row in rows)
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

    /// <summary>One candle row: <c>[ts, open, high, low, close, baseVolume, quoteVolume, usdtVolume]</c>.</summary>
    private static Bar? Row(JsonElement row, double scale)
    {
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6) return null;
        return new Bar(
            CryptoConvert.MsUtc(CryptoConvert.L(row[0])),
            CryptoConvert.D(row[1]), CryptoConvert.D(row[2]), CryptoConvert.D(row[3]), CryptoConvert.D(row[4]),
            CryptoConvert.ToSize(CryptoConvert.D(row[5]), scale));
    }


    internal static string LiveInterval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1m",
        BarSize.ThreeMinutes => "3m",
        BarSize.FiveMinutes => "5m",
        BarSize.FifteenMinutes => "15m",
        BarSize.OneHour => "1H",
        BarSize.OneDay => "1D",
        _ => "1m",
    };

    internal static string RestInterval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1min",
        BarSize.ThreeMinutes => "3min",
        BarSize.FiveMinutes => "5min",
        BarSize.FifteenMinutes => "15min",
        BarSize.OneHour => "1h",
        BarSize.OneDay => "1day",
        _ => "1min",
    };
}
