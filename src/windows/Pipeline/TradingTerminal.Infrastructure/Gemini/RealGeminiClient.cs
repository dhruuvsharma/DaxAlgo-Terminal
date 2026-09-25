using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Gemini;

/// <summary>
/// Gemini spot over the public v2 market-data socket (no key, no account). One <c>l2</c> subscription
/// carries both the book (<c>l2_updates</c>: <c>[side, price, quantity]</c> changes, quantity 0 removes)
/// and the tape (<c>trade</c> messages), so L1, L2 and trades are all read from it. Bars ←
/// <c>candles_*</c> live and <c>/v2/candles</c> for history.
///
/// <para><b>There is no ticker channel.</b> L1 is the top of the book this client maintains from
/// <c>l2_updates</c>. The first <c>l2_updates</c> after subscribing is the whole book, and it also carries
/// a <c>trades</c> array of recent prints — history, not live flow, so it is not re-emitted.</para>
///
/// <para>Gemini publishes no three-minute candle; those are rolled up from one-minute bars.</para>
/// </summary>
internal sealed class RealGeminiClient : PublicCryptoClient<GeminiOptions>
{
    public RealGeminiClient(ILogger<RealGeminiClient> logger, IOptions<GeminiOptions> options)
        : base(logger, options.Value) { }

    public override BrokerKind Kind => BrokerKind.Gemini;
    protected override string VenueName => "Gemini";
    protected override string ExchangeCode => "GEMINI";
    protected override string ConnectCheckPath => "/v1/pubticker/btcusd";

    protected override string Symbol(Contract contract) => contract.Symbol.Trim().ToUpperInvariant();

    public override IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default)
    {
        var book = new L2OrderBook();
        return Json(Sub("l2", Symbol(contract), book.Clear), "l2", el => ParseTop(el, book, Options.SizeScale), ct);
    }

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default)
    {
        var book = new L2OrderBook();
        return Json(Sub("l2", Symbol(contract), book.Clear), "l2", el => ParseBook(el, book, levels, Options.SizeScale), ct);
    }

    public override IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        Json(Sub("l2", Symbol(contract), null), "l2", el => ParseTrade(el, Options.SizeScale), ct);

    protected override bool HasRestInterval(BarSize size) => size != BarSize.ThreeMinutes;
    protected override bool HasLiveInterval(BarSize size) => size != BarSize.ThreeMinutes;

    protected override IAsyncEnumerable<Bar> StreamBarsAsync(string symbol, BarSize size, CancellationToken ct)
    {
        // The first candles message is a day of history, newest first; after it, each carries the one
        // candle that changed. Only the history's newest row is live.
        var first = true;
        return Json(Sub("candles_" + LiveInterval(size), symbol, () => first = true), "candles",
            el =>
            {
                var bars = ParseCandles(el, Options.SizeScale, onlyNewest: first);
                if (bars.Count > 0) first = false;
                return bars;
            }, ct);
    }

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, int count, CancellationToken ct)
    {
        var url = $"{Options.RestBaseUrl}/v2/candles/{symbol.ToLowerInvariant()}/{RestInterval(size)}";
        var (doc, body) = await GetJsonAsync(url, ct).ConfigureAwait(false);
        using (doc)
            return WireFormat.OrWarn(ParseRows(doc.RootElement, Options.SizeScale), body, Logger, VenueName, "candles");
    }

    private CryptoStreamSpec Sub(string name, string symbol, Action? onConnected) => new()
    {
        Name = VenueName,
        Url = CryptoStreamSpec.Fixed(Options.WsBaseUrl),
        Subscribe = [$"{{\"type\":\"subscribe\",\"subscriptions\":[{{\"name\":\"{name}\",\"symbols\":[\"{symbol}\"]}}]}}"],
        // A reconnect starts from a fresh full book; whatever the old one held is stale.
        OnConnected = onConnected is null ? null : _ => { onConnected(); return Task.CompletedTask; },
        InitialDelaySeconds = Options.ReconnectInitialDelaySeconds,
        MaxDelaySeconds = Options.ReconnectMaxDelaySeconds,
    };

    // ── Parsers ─────────────────────────────────────────────────────────────────────────────────

    private static bool IsType(JsonElement el, string type) =>
        el.TryGetProperty("type", out var t) && t.GetString() == type;

    /// <summary>Applies an <c>l2_updates</c> message; false for anything else.</summary>
    internal static bool Apply(JsonElement el, L2OrderBook book)
    {
        if (!IsType(el, "l2_updates") || !el.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var change in changes.EnumerateArray())
            if (change.ValueKind == JsonValueKind.Array && change.GetArrayLength() >= 3)
                book.Apply(change[0].GetString() == "buy", CryptoConvert.D(change[1]), CryptoConvert.D(change[2]));
        return true;
    }

    internal static IEnumerable<Tick> ParseTop(JsonElement el, L2OrderBook book, double scale)
    {
        if (!Apply(el, book) || book.IsEmpty) yield break;
        var top = book.Snapshot(1, scale);
        yield return new Tick(top.TimestampUtc, top.BestBid, top.BestAsk, top.BestBidSize, top.BestAskSize);
    }

    internal static IEnumerable<DepthSnapshot> ParseBook(JsonElement el, L2OrderBook book, int levels, double scale)
    {
        if (!Apply(el, book) || book.IsEmpty) yield break;
        yield return book.Snapshot(levels, scale);
    }

    internal static IEnumerable<TradeTick> ParseTrade(JsonElement el, double scale)
    {
        if (!IsType(el, "trade")) yield break;
        yield return new TradeTick(
            CryptoConvert.MsUtc(el, "timestamp"),
            CryptoConvert.D(el, "price"),
            CryptoConvert.ToSize(CryptoConvert.D(el, "quantity"), scale),
            el.TryGetProperty("side", out var side) && side.GetString() == "sell" ? AggressorSide.Sell : AggressorSide.Buy);
    }

    internal static IReadOnlyList<Bar> ParseCandles(JsonElement el, double scale, bool onlyNewest)
    {
        if (!el.TryGetProperty("type", out var type) || type.GetString() is not { } name
            || !name.StartsWith("candles_", StringComparison.Ordinal)
            || !el.TryGetProperty("changes", out var changes))
            return [];

        var rows = ParseRows(changes, scale).OrderBy(b => b.TimestampUtc).ToList();
        return onlyNewest && rows.Count > 0 ? [rows[^1]] : rows;
    }

    /// <summary><c>[[time (ms), open, high, low, close, volume], …]</c>, newest first.</summary>
    internal static IReadOnlyList<Bar> ParseRows(JsonElement rows, double scale)
    {
        var bars = new List<Bar>();
        if (rows.ValueKind != JsonValueKind.Array) return bars;
        foreach (var row in rows.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 6)
                bars.Add(new Bar(
                    CryptoConvert.MsUtc(CryptoConvert.L(row[0])),
                    CryptoConvert.D(row[1]), CryptoConvert.D(row[2]), CryptoConvert.D(row[3]), CryptoConvert.D(row[4]),
                    CryptoConvert.ToSize(CryptoConvert.D(row[5]), scale)));
        return bars;
    }

    internal static string LiveInterval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1m",
        BarSize.FiveMinutes => "5m",
        BarSize.FifteenMinutes => "15m",
        BarSize.OneHour => "1h",
        BarSize.OneDay => "1d",
        _ => "1m",
    };

    internal static string RestInterval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1m",
        BarSize.FiveMinutes => "5m",
        BarSize.FifteenMinutes => "15m",
        BarSize.OneHour => "1hr",
        BarSize.OneDay => "1day",
        _ => "1m",
    };
}
