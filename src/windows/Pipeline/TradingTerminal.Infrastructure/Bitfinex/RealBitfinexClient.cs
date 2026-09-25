using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Bitfinex;

/// <summary>
/// Bitfinex spot over the public v2 socket (no key, no account). L1 ← <c>ticker</c>, L2 ← <c>book</c>
/// (P0, 25 levels), trades ← <c>trades</c>, bars ← <c>candles</c> live and <c>/v2/candles/…/hist</c>
/// for history.
///
/// <para><b>Arrays, not objects.</b> Data arrives as <c>[chanId, payload]</c>; heartbeats as
/// <c>[chanId, "hb"]</c>. Each subscription here has a socket to itself, so the channel id need not be
/// tracked — any array frame is ours.</para>
///
/// <para><b>Book rows are <c>[price, count, amount]</c></b>: the sign of amount is the side (positive
/// bid), and a count of zero deletes the level whatever the amount says. Trades are
/// <c>[id, mts, amount, price]</c>, sell when amount is negative; only <c>te</c> (executed) is read —
/// <c>tu</c> repeats the same print. Candle rows are <c>[mts, open, CLOSE, HIGH, LOW, volume]</c>.</para>
///
/// <para>A three-minute candle subscription is <i>accepted</i> and answered with an empty array, then
/// nothing — so three-minute bars are rolled up from one-minute ones.</para>
/// </summary>
internal sealed class RealBitfinexClient : PublicCryptoClient<BitfinexOptions>
{
    public RealBitfinexClient(ILogger<RealBitfinexClient> logger, IOptions<BitfinexOptions> options)
        : base(logger, options.Value) { }

    public override BrokerKind Kind => BrokerKind.Bitfinex;
    protected override string VenueName => "Bitfinex";
    protected override string ExchangeCode => "BITFINEX";
    protected override string ConnectCheckPath => "/v2/platform/status";

    /// <summary>The trading-pair symbol the API wants: <c>t</c> + the pair. Pairs are upper-case, so a
    /// leading lower-case <c>t</c> means someone typed the prefix already.</summary>
    protected override string Symbol(Contract contract)
    {
        var pair = contract.Symbol.Trim();
        return pair.StartsWith('t') ? pair : "t" + pair.ToUpperInvariant();
    }

    public override IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
        Json(Sub($"{{\"event\":\"subscribe\",\"channel\":\"ticker\",\"symbol\":\"{Symbol(contract)}\"}}"), "ticker",
            el => ParseTicker(el, Options.SizeScale), ct);

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default)
    {
        var book = new L2OrderBook();
        var len = Options.DepthLevels switch { <= 1 => 1, <= 25 => 25, <= 100 => 100, _ => 250 };
        return Json(Sub($"{{\"event\":\"subscribe\",\"channel\":\"book\",\"symbol\":\"{Symbol(contract)}\",\"prec\":\"P0\",\"freq\":\"F0\",\"len\":\"{len}\"}}", book.Clear),
            "book", el => ParseBook(el, book, levels, Options.SizeScale), ct);
    }

    public override IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        Json(Sub($"{{\"event\":\"subscribe\",\"channel\":\"trades\",\"symbol\":\"{Symbol(contract)}\"}}"), "trades",
            el => ParseTrade(el, Options.SizeScale), ct);

    protected override bool HasRestInterval(BarSize size) => size != BarSize.ThreeMinutes;
    protected override bool HasLiveInterval(BarSize size) => size != BarSize.ThreeMinutes;

    protected override IAsyncEnumerable<Bar> StreamBarsAsync(string symbol, BarSize size, CancellationToken ct) =>
        Json(Sub($"{{\"event\":\"subscribe\",\"channel\":\"candles\",\"key\":\"trade:{Interval(size)}:{symbol}\"}}"), "candles",
            el => ParseCandles(el, Options.SizeScale), ct);

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, int count, CancellationToken ct)
    {
        // sort=-1 is newest first; without it `limit` counts forward from the first candle ever traded.
        var url = $"{Options.RestBaseUrl}/v2/candles/trade:{Interval(size)}:{symbol}/hist?limit={count}&sort=-1";
        var (doc, body) = await GetJsonAsync(url, ct).ConfigureAwait(false);
        using (doc)
            return WireFormat.OrWarn(ParseRows(doc.RootElement, Options.SizeScale), body, Logger, VenueName, "candles");
    }

    private CryptoStreamSpec Sub(string subscribe, Action? onConnected = null) => new()
    {
        Name = VenueName,
        Url = CryptoStreamSpec.Fixed(Options.WsBaseUrl),
        Subscribe = [subscribe],
        OnConnected = onConnected is null ? null : _ => { onConnected(); return Task.CompletedTask; },
        InitialDelaySeconds = Options.ReconnectInitialDelaySeconds,
        MaxDelaySeconds = Options.ReconnectMaxDelaySeconds,
    };

    // ── Parsers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The payload of a <c>[chanId, payload…]</c> frame; false for events and heartbeats.</summary>
    private static bool Payload(JsonElement el, out JsonElement payload)
    {
        payload = default;
        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() < 2) return false;
        payload = el[1];
        return payload.ValueKind == JsonValueKind.Array;
    }

    internal static IEnumerable<Tick> ParseTicker(JsonElement el, double scale)
    {
        if (!Payload(el, out var p) || p.GetArrayLength() < 4) yield break;
        yield return new Tick(DateTime.UtcNow,
            CryptoConvert.D(p[0]), CryptoConvert.D(p[2]),
            CryptoConvert.ToSize(CryptoConvert.D(p[1]), scale),
            CryptoConvert.ToSize(CryptoConvert.D(p[3]), scale));
    }

    internal static IEnumerable<DepthSnapshot> ParseBook(JsonElement el, L2OrderBook book, int levels, double scale)
    {
        if (!Payload(el, out var p) || p.GetArrayLength() == 0) yield break;

        if (p[0].ValueKind == JsonValueKind.Array)
        {
            book.Clear();
            foreach (var row in p.EnumerateArray()) ApplyRow(book, row);
        }
        else
        {
            ApplyRow(book, p);
        }

        if (!book.IsEmpty) yield return book.Snapshot(levels, scale);
    }

    private static void ApplyRow(L2OrderBook book, JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 3) return;
        var price = CryptoConvert.D(row[0]);
        var count = CryptoConvert.D(row[1]);
        var amount = CryptoConvert.D(row[2]);
        book.Apply(isBid: amount > 0, price, count == 0 ? 0 : Math.Abs(amount));
    }

    internal static IEnumerable<TradeTick> ParseTrade(JsonElement el, double scale)
    {
        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() < 3
            || el[1].ValueKind != JsonValueKind.String || el[1].GetString() != "te")
            yield break;

        var t = el[2];
        if (t.ValueKind != JsonValueKind.Array || t.GetArrayLength() < 4) yield break;
        var amount = CryptoConvert.D(t[2]);
        yield return new TradeTick(
            CryptoConvert.MsUtc(CryptoConvert.L(t[1])),
            CryptoConvert.D(t[3]),
            CryptoConvert.ToSize(Math.Abs(amount), scale),
            amount < 0 ? AggressorSide.Sell : AggressorSide.Buy);
    }

    internal static IReadOnlyList<Bar> ParseCandles(JsonElement el, double scale)
    {
        if (!Payload(el, out var p) || p.GetArrayLength() == 0) return [];

        // A snapshot is an array of rows, newest first; only its newest is live. An update is one row.
        if (p[0].ValueKind == JsonValueKind.Array)
        {
            var rows = ParseRows(p, scale);
            return rows.Count == 0 ? [] : [rows.MaxBy(b => b.TimestampUtc)!];
        }

        return Row(p, scale) is { } bar ? [bar] : [];
    }

    internal static IReadOnlyList<Bar> ParseRows(JsonElement rows, double scale)
    {
        var bars = new List<Bar>();
        if (rows.ValueKind != JsonValueKind.Array) return bars;
        foreach (var row in rows.EnumerateArray())
            if (Row(row, scale) is { } bar) bars.Add(bar);
        return bars;
    }

    /// <summary><c>[mts, open, close, high, low, volume]</c>.</summary>
    private static Bar? Row(JsonElement row, double scale)
    {
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6) return null;
        return new Bar(
            CryptoConvert.MsUtc(CryptoConvert.L(row[0])),
            Open: CryptoConvert.D(row[1]),
            High: CryptoConvert.D(row[3]),
            Low: CryptoConvert.D(row[4]),
            Close: CryptoConvert.D(row[2]),
            Volume: CryptoConvert.ToSize(CryptoConvert.D(row[5]), scale));
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
