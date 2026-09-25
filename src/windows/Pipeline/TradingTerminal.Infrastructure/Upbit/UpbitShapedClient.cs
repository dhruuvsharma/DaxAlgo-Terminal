using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Upbit;

/// <summary>
/// The Upbit wire format, which Bithumb's current API copies field for field.
///
/// <para>The socket takes one JSON array per subscription — <c>[{"ticket":…},{"type":…,"codes":[…]}]</c> —
/// and answers in <b>binary</b> frames that contain JSON. L1 and L2 both come from <c>orderbook</c>, a full
/// snapshot per push; trades from <c>trade</c>, whose <c>ask_bid</c> names the aggressor (<c>ASK</c> is a
/// seller hitting the bid). Each <c>trade</c> subscription opens with recent prints marked
/// <c>stream_type:"SNAPSHOT"</c>; those are history and are skipped.</para>
///
/// <para>Markets are written quote first — <c>KRW-BTC</c> is bitcoin priced in won.</para>
///
/// <para>REST candles are capped at 200 per request and paged backwards with <c>to</c>.</para>
/// </summary>
internal abstract class UpbitShapedClient<TOptions> : PublicCryptoClient<TOptions> where TOptions : CryptoVenueOptions
{
    protected UpbitShapedClient(ILogger logger, TOptions options) : base(logger, options) { }

    /// <summary>Where this venue's trading day starts, as an offset from UTC midnight. Upbit's daily candle
    /// opens at 00:00 UTC; Bithumb's at 00:00 Korea time, which is 15:00 UTC the day before.</summary>
    protected abstract TimeSpan DayStartOffset { get; }

    protected override string ConnectCheckPath => "/v1/market/all";

    protected override string Symbol(Contract contract) => contract.Symbol.Trim().ToUpperInvariant();

    protected override string QuoteOf(string symbol)
    {
        var dash = symbol.IndexOf('-');
        return dash > 0 ? symbol[..dash].ToUpperInvariant() : "KRW";
    }

    public override IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
        Json(Sub("orderbook", Symbol(contract)), "orderbook", el => ParseTop(el, Options.SizeScale), ct);

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default) =>
        Json(Sub("orderbook", Symbol(contract)), "orderbook", el => ParseBook(el, levels, Options.SizeScale), ct);

    public override IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        Json(Sub("trade", Symbol(contract)), "trade", el => ParseTrade(el, Options.SizeScale), ct);

    protected override bool HasRestInterval(BarSize size) => true;

    /// <summary>Daily bars, live, from the ticker's running day open / high / low / last / volume — exact
    /// from the first frame, which a rollup of minutes seen since subscribing could not be.</summary>
    protected async IAsyncEnumerable<Bar> DailyBarsFromTickerAsync(
        Contract contract, [EnumeratorCancellation] CancellationToken ct)
    {
        var offset = DayStartOffset;
        await foreach (var bar in Json(Sub("ticker", Symbol(contract)), "ticker",
                           el => ParseDailyTicker(el, offset, Options.SizeScale), ct).WithCancellation(ct).ConfigureAwait(false))
            yield return bar;
    }

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, int count, CancellationToken ct)
    {
        var path = size switch
        {
            BarSize.OneMinute => "minutes/1",
            BarSize.ThreeMinutes => "minutes/3",
            BarSize.FiveMinutes => "minutes/5",
            BarSize.FifteenMinutes => "minutes/15",
            BarSize.OneHour => "minutes/60",
            BarSize.OneDay => "days",
            _ => "minutes/1",
        };

        var bars = new List<Bar>();
        string? to = null;
        for (var page = 0; page < 5 && bars.Count < count; page++)
        {
            var want = Math.Min(200, count - bars.Count);
            var url = $"{Options.RestBaseUrl}/v1/candles/{path}?market={symbol}&count={want}"
                + (to is null ? string.Empty : $"&to={Uri.EscapeDataString(to)}");
            var (doc, body) = await GetJsonAsync(url, ct).ConfigureAwait(false);
            using (doc)
            {
                var pageBars = WireFormat.OrWarn(ParseRestCandles(doc.RootElement, Options.SizeScale), body, Logger, VenueName, "candles");
                if (pageBars.Count == 0) break;
                bars.AddRange(pageBars);

                // `to` is exclusive and read as UTC when it carries a Z.
                to = pageBars.Min(b => b.TimestampUtc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                if (pageBars.Count < want) break;
            }
        }

        return bars;
    }

    protected CryptoStreamSpec Sub(string type, string code) => new()
    {
        Name = VenueName,
        Url = CryptoStreamSpec.Fixed(Options.WsBaseUrl),
        Subscribe = [$"[{{\"ticket\":\"{Guid.NewGuid():N}\"}},{{\"type\":\"{type}\",\"codes\":[\"{code}\"]}}]"],
        // An idle socket is closed after 120 s; PING is answered with {"status":"UP"}.
        Ping = "PING",
        PingIntervalSeconds = 30,
        InitialDelaySeconds = Options.ReconnectInitialDelaySeconds,
        MaxDelaySeconds = Options.ReconnectMaxDelaySeconds,
    };

    // ── Parsers ─────────────────────────────────────────────────────────────────────────────────

    private static bool IsType(JsonElement el, string type) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty("type", out var t) && t.GetString() == type;

    private static bool IsSnapshot(JsonElement el) =>
        el.TryGetProperty("stream_type", out var s) && s.GetString() == "SNAPSHOT";

    internal static L2OrderBook? Book(JsonElement el)
    {
        if (!IsType(el, "orderbook") || !el.TryGetProperty("orderbook_units", out var units) || units.ValueKind != JsonValueKind.Array)
            return null;

        var book = new L2OrderBook();
        foreach (var unit in units.EnumerateArray())
        {
            book.Apply(isBid: true, CryptoConvert.D(unit, "bid_price"), CryptoConvert.D(unit, "bid_size"));
            book.Apply(isBid: false, CryptoConvert.D(unit, "ask_price"), CryptoConvert.D(unit, "ask_size"));
        }

        return book;
    }

    internal static IEnumerable<Tick> ParseTop(JsonElement el, double scale)
    {
        if (Book(el) is not { IsEmpty: false } book) yield break;
        var top = book.Snapshot(1, scale, CryptoConvert.MsUtc(el, "timestamp"));
        yield return new Tick(top.TimestampUtc, top.BestBid, top.BestAsk, top.BestBidSize, top.BestAskSize);
    }

    internal static IEnumerable<DepthSnapshot> ParseBook(JsonElement el, int levels, double scale)
    {
        if (Book(el) is not { IsEmpty: false } book) yield break;
        yield return book.Snapshot(levels, scale, CryptoConvert.MsUtc(el, "timestamp"));
    }

    internal static IEnumerable<TradeTick> ParseTrade(JsonElement el, double scale)
    {
        if (!IsType(el, "trade") || IsSnapshot(el)) yield break;
        yield return new TradeTick(
            CryptoConvert.MsUtc(el, "trade_timestamp"),
            CryptoConvert.D(el, "trade_price"),
            CryptoConvert.ToSize(CryptoConvert.D(el, "trade_volume"), scale),
            el.TryGetProperty("ask_bid", out var side) && side.GetString() == "ASK" ? AggressorSide.Sell : AggressorSide.Buy);
    }

    /// <summary>A <c>candle.*</c> push. The first one after subscribing is marked SNAPSHOT but is the
    /// candle still forming, so it is kept.</summary>
    internal static IEnumerable<Bar> ParseCandle(JsonElement el, double scale)
    {
        if (!el.TryGetProperty("type", out var t) || t.GetString() is not { } type
            || !type.StartsWith("candle.", StringComparison.Ordinal))
            yield break;
        if (Candle(el, scale) is { } bar) yield return bar;
    }

    internal static IEnumerable<Bar> ParseDailyTicker(JsonElement el, TimeSpan dayStart, double scale)
    {
        if (!IsType(el, "ticker")) yield break;
        var now = CryptoConvert.MsUtc(el, "timestamp");
        var bucket = now.Date.Add(dayStart);
        if (bucket > now) bucket = bucket.AddDays(-1);
        else if (bucket.AddDays(1) <= now) bucket = bucket.AddDays(1);
        yield return new Bar(
            DateTime.SpecifyKind(bucket, DateTimeKind.Utc),
            CryptoConvert.D(el, "opening_price"), CryptoConvert.D(el, "high_price"),
            CryptoConvert.D(el, "low_price"), CryptoConvert.D(el, "trade_price"),
            CryptoConvert.ToSize(CryptoConvert.D(el, "acc_trade_volume"), scale));
    }

    internal static IReadOnlyList<Bar> ParseRestCandles(JsonElement root, double scale)
    {
        var bars = new List<Bar>();
        if (root.ValueKind != JsonValueKind.Array) return bars;
        foreach (var row in root.EnumerateArray())
            if (Candle(row, scale) is { } bar) bars.Add(bar);
        return bars;
    }

    /// <summary>A candle object, REST or socket — the two share field names.</summary>
    private static Bar? Candle(JsonElement row, double scale)
    {
        if (row.ValueKind != JsonValueKind.Object
            || !row.TryGetProperty("candle_date_time_utc", out var start)
            || !DateTime.TryParse(start.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
            return null;

        return new Bar(
            time,
            CryptoConvert.D(row, "opening_price"), CryptoConvert.D(row, "high_price"),
            CryptoConvert.D(row, "low_price"), CryptoConvert.D(row, "trade_price"),
            CryptoConvert.ToSize(CryptoConvert.D(row, "candle_acc_trade_volume"), scale));
    }
}
