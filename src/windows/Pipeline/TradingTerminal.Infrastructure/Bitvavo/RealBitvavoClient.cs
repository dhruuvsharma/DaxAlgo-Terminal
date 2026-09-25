using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Bitvavo;

/// <summary>
/// Bitvavo — the largest euro venue — over the public v2 socket (no key, no account). L1 ←
/// <c>ticker</c>, L2 ← <c>book</c> on top of a REST snapshot, trades ← <c>trades</c>, bars ←
/// <c>candles</c> live and <c>/v2/{market}/candles</c> for history.
///
/// <para><b>The ticker sends only what changed.</b> A push carrying a new best ask omits the bid
/// entirely, so the last known value of each field is kept and a tick is emitted once both sides are
/// known.</para>
///
/// <para><b>The book channel is deltas only</b>, each numbered with a <c>nonce</c>. On every connection
/// a REST snapshot is fetched after subscribing — so no delta can fall between the two — deltas the
/// snapshot already contains are skipped, and a gap in the numbering forces a reconnect and a fresh
/// snapshot rather than showing a book with a hole in it.</para>
///
/// <para>No three-minute candle is published; those are rolled up from one-minute bars.</para>
/// </summary>
internal sealed class RealBitvavoClient : PublicCryptoClient<BitvavoOptions>
{
    public RealBitvavoClient(ILogger<RealBitvavoClient> logger, IOptions<BitvavoOptions> options)
        : base(logger, options.Value) { }

    public override BrokerKind Kind => BrokerKind.Bitvavo;
    protected override string VenueName => "Bitvavo";
    protected override string ExchangeCode => "BITVAVO";
    protected override string ConnectCheckPath => "/v2/time";

    protected override string Symbol(Contract contract) => contract.Symbol.Trim().ToUpperInvariant();

    public override IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default)
    {
        var state = new TickerState();
        return Json(Sub("ticker", Symbol(contract), onConnected: _ => { state.Reset(); return Task.CompletedTask; }),
            "ticker", el => ParseTicker(el, state, Options.SizeScale), ct);
    }

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default)
    {
        var market = Symbol(contract);
        var book = new NonceBook();
        return Json(Sub("book", market, onConnected: async token =>
            {
                var (doc, _) = await GetJsonAsync($"{Options.RestBaseUrl}/v2/{market}/book?depth={Options.DepthLevels}", token).ConfigureAwait(false);
                using (doc) book.Seed(doc.RootElement);
            }),
            "book", el => ParseBookUpdate(el, book, levels, Options.SizeScale), ct);
    }

    public override IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        Json(Sub("trades", Symbol(contract)), "trades", el => ParseTrade(el, Options.SizeScale), ct);

    protected override bool HasRestInterval(BarSize size) => size != BarSize.ThreeMinutes;
    protected override bool HasLiveInterval(BarSize size) => size != BarSize.ThreeMinutes;

    protected override IAsyncEnumerable<Bar> StreamBarsAsync(string symbol, BarSize size, CancellationToken ct) =>
        Json(Sub("candles", symbol, interval: Interval(size)), "candles", el => ParseCandle(el, Options.SizeScale), ct);

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, int count, CancellationToken ct)
    {
        var url = $"{Options.RestBaseUrl}/v2/{symbol}/candles?interval={Interval(size)}&limit={Math.Min(count, 1440)}";
        var (doc, body) = await GetJsonAsync(url, ct).ConfigureAwait(false);
        using (doc)
            return WireFormat.OrWarn(ParseRows(doc.RootElement, Options.SizeScale), body, Logger, VenueName, "candles");
    }

    private CryptoStreamSpec Sub(string channel, string market, string? interval = null, Func<CancellationToken, Task>? onConnected = null)
    {
        var intervalJson = interval is null ? string.Empty : $",\"interval\":[\"{interval}\"]";
        return new CryptoStreamSpec
        {
            Name = VenueName,
            Url = CryptoStreamSpec.Fixed(Options.WsBaseUrl),
            Subscribe = [$"{{\"action\":\"subscribe\",\"channels\":[{{\"name\":\"{channel}\"{intervalJson},\"markets\":[\"{market}\"]}}]}}"],
            OnConnected = onConnected,
            InitialDelaySeconds = Options.ReconnectInitialDelaySeconds,
            MaxDelaySeconds = Options.ReconnectMaxDelaySeconds,
        };
    }

    // ── State ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The last known value of each best-bid/ask field.</summary>
    internal sealed class TickerState
    {
        public double Bid, BidSize, Ask, AskSize;

        public void Reset() => Bid = BidSize = Ask = AskSize = 0;
    }

    /// <summary>A book laid on a REST snapshot, with the nonce of the last delta applied.</summary>
    internal sealed class NonceBook
    {
        public L2OrderBook Book { get; } = new();

        public long Nonce { get; private set; } = -1;

        public void Seed(JsonElement snapshot)
        {
            Book.Clear();
            CryptoConvert.ApplyLevels(Book, snapshot, "bids", isBid: true);
            CryptoConvert.ApplyLevels(Book, snapshot, "asks", isBid: false);
            Nonce = snapshot.TryGetProperty("nonce", out var n) ? CryptoConvert.L(n) : -1;
        }

        /// <summary>True when the delta was applied; false when the snapshot already holds it.</summary>
        public bool Apply(JsonElement update)
        {
            var nonce = update.TryGetProperty("nonce", out var n) ? CryptoConvert.L(n) : -1;
            if (Nonce < 0) throw new CryptoStreamResyncException("book delta arrived before a snapshot");
            if (nonce <= Nonce) return false;
            if (nonce > Nonce + 1) throw new CryptoStreamResyncException($"book nonce jumped from {Nonce} to {nonce}");

            CryptoConvert.ApplyLevels(Book, update, "bids", isBid: true);
            CryptoConvert.ApplyLevels(Book, update, "asks", isBid: false);
            Nonce = nonce;
            return true;
        }
    }

    // ── Parsers ─────────────────────────────────────────────────────────────────────────────────

    private static bool IsEvent(JsonElement el, string name) =>
        el.TryGetProperty("event", out var e) && e.GetString() == name;

    internal static IEnumerable<Tick> ParseTicker(JsonElement el, TickerState state, double scale)
    {
        if (!IsEvent(el, "ticker")) yield break;
        if (el.TryGetProperty("bestBid", out var b)) state.Bid = CryptoConvert.D(b);
        if (el.TryGetProperty("bestBidSize", out var bs)) state.BidSize = CryptoConvert.D(bs);
        if (el.TryGetProperty("bestAsk", out var a)) state.Ask = CryptoConvert.D(a);
        if (el.TryGetProperty("bestAskSize", out var @as)) state.AskSize = CryptoConvert.D(@as);
        if (state.Bid <= 0 || state.Ask <= 0) yield break;

        yield return new Tick(DateTime.UtcNow, state.Bid, state.Ask,
            CryptoConvert.ToSize(state.BidSize, scale), CryptoConvert.ToSize(state.AskSize, scale));
    }

    internal static IEnumerable<DepthSnapshot> ParseBookUpdate(JsonElement el, NonceBook book, int levels, double scale)
    {
        if (!IsEvent(el, "book") || !book.Apply(el) || book.Book.IsEmpty) yield break;
        var nanos = el.TryGetProperty("timestamp", out var t) ? CryptoConvert.L(t) : 0;
        yield return book.Book.Snapshot(levels, scale, CryptoConvert.MsUtc(nanos / 1_000_000));
    }

    internal static IEnumerable<TradeTick> ParseTrade(JsonElement el, double scale)
    {
        if (!IsEvent(el, "trade")) yield break;
        yield return new TradeTick(
            CryptoConvert.MsUtc(el, "timestamp"),
            CryptoConvert.D(el, "price"),
            CryptoConvert.ToSize(CryptoConvert.D(el, "amount"), scale),
            el.TryGetProperty("side", out var side) && side.GetString() == "sell" ? AggressorSide.Sell : AggressorSide.Buy);
    }

    internal static IEnumerable<Bar> ParseCandle(JsonElement el, double scale) =>
        IsEvent(el, "candle") && el.TryGetProperty("candle", out var rows) ? ParseRows(rows, scale) : [];

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

    internal static string Interval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1m",
        BarSize.FiveMinutes => "5m",
        BarSize.FifteenMinutes => "15m",
        BarSize.OneHour => "1h",
        BarSize.OneDay => "1d",
        _ => "1m",
    };
}
