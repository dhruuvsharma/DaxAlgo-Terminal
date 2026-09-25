using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Bitstamp;

/// <summary>
/// Bitstamp spot over the public socket (no key, no account). L1 and L2 ← <c>order_book_{pair}</c> (a
/// hundred-level snapshot per push), trades ← <c>live_trades_{pair}</c> (<c>type</c> 0 is a buy, 1 a
/// sell), history ← <c>/api/v2/ohlc</c>, which serves every size including three minutes.
///
/// <para><b>No candle stream.</b> Live intraday bars are built from the trade tape. The daily bar is
/// polled from REST instead, because one built from trades would open at whatever traded first after
/// subscribing rather than at the day's real open.</para>
/// </summary>
internal sealed class RealBitstampClient : PublicCryptoClient<BitstampOptions>
{
    private static readonly TimeSpan DailyPoll = TimeSpan.FromSeconds(15);

    public RealBitstampClient(ILogger<RealBitstampClient> logger, IOptions<BitstampOptions> options)
        : base(logger, options.Value) { }

    public override BrokerKind Kind => BrokerKind.Bitstamp;
    protected override string VenueName => "Bitstamp";
    protected override string ExchangeCode => "BITSTAMP";
    protected override string ConnectCheckPath => "/api/v2/ohlc/btcusd/?step=60&limit=1";

    protected override string Symbol(Contract contract) => contract.Symbol.Trim().ToLowerInvariant();

    public override IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
        Json(Sub($"order_book_{Symbol(contract)}"), "order_book", el => ParseTop(el, Options.SizeScale), ct);

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default) =>
        Json(Sub($"order_book_{Symbol(contract)}"), "order_book", el => ParseBook(el, levels, Options.SizeScale), ct);

    public override IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        Json(Sub($"live_trades_{Symbol(contract)}"), "live_trades", el => ParseTrade(el, Options.SizeScale), ct);

    protected override bool HasRestInterval(BarSize size) => true;
    protected override bool HasLiveInterval(BarSize size) => false;

    protected override IAsyncEnumerable<Bar> StreamBarsAsync(string symbol, BarSize size, CancellationToken ct) =>
        throw new NotSupportedException("Bitstamp publishes no candle stream.");

    protected override IAsyncEnumerable<Bar> SynthesiseBarsAsync(Contract contract, BarSize size, CancellationToken ct) =>
        size == BarSize.OneDay ? PollDailyAsync(Symbol(contract), ct) : BarsFromTradesAsync(contract, size, ct);

    private async IAsyncEnumerable<Bar> PollDailyAsync(string pair, [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<Bar> latest = [];
            try { latest = await FetchBarsAsync(pair, BarSize.OneDay, 1, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { yield break; }
            catch (Exception ex) { Logger.LogDebug(ex, "Bitstamp daily poll failed; retrying."); }

            if (latest.Count > 0) yield return latest[^1];

            try { await Task.Delay(DailyPoll, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, int count, CancellationToken ct)
    {
        var step = (int)size.ToTimeSpan().TotalSeconds;
        var url = $"{Options.RestBaseUrl}/api/v2/ohlc/{symbol}/?step={step}&limit={Math.Min(count, 1000)}";
        var (doc, body) = await GetJsonAsync(url, ct).ConfigureAwait(false);
        using (doc)
            return WireFormat.OrWarn(ParseOhlc(doc.RootElement, Options.SizeScale), body, Logger, VenueName, "ohlc");
    }

    private CryptoStreamSpec Sub(string channel) => new()
    {
        Name = VenueName,
        Url = CryptoStreamSpec.Fixed(Options.WsBaseUrl),
        Subscribe = [$"{{\"event\":\"bts:subscribe\",\"data\":{{\"channel\":\"{channel}\"}}}}"],
        Ping = "{\"event\":\"bts:heartbeat\"}",
        PingIntervalSeconds = 20,
        InitialDelaySeconds = Options.ReconnectInitialDelaySeconds,
        MaxDelaySeconds = Options.ReconnectMaxDelaySeconds,
    };

    // ── Parsers ─────────────────────────────────────────────────────────────────────────────────

    private static bool Data(JsonElement el, string @event, out JsonElement data)
    {
        data = default;
        return el.TryGetProperty("event", out var e) && e.GetString() == @event
            && el.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Object;
    }

    /// <summary>Bitstamp stamps events in microseconds, as a string.</summary>
    private static DateTime Micros(JsonElement data) =>
        CryptoConvert.MsUtc(data.TryGetProperty("microtimestamp", out var us) ? CryptoConvert.L(us) / 1000 : 0);

    private static L2OrderBook? Book(JsonElement el, out DateTime time)
    {
        time = default;
        if (!Data(el, "data", out var d) || !d.TryGetProperty("bids", out _)) return null;
        var book = new L2OrderBook();
        CryptoConvert.ApplyLevels(book, d, "bids", isBid: true);
        CryptoConvert.ApplyLevels(book, d, "asks", isBid: false);
        time = Micros(d);
        return book;
    }

    internal static IEnumerable<Tick> ParseTop(JsonElement el, double scale)
    {
        if (Book(el, out var time) is not { IsEmpty: false } book) yield break;
        var top = book.Snapshot(1, scale, time);
        yield return new Tick(time, top.BestBid, top.BestAsk, top.BestBidSize, top.BestAskSize);
    }

    internal static IEnumerable<DepthSnapshot> ParseBook(JsonElement el, int levels, double scale)
    {
        if (Book(el, out var time) is not { IsEmpty: false } book) yield break;
        yield return book.Snapshot(levels, scale, time);
    }

    internal static IEnumerable<TradeTick> ParseTrade(JsonElement el, double scale)
    {
        if (!Data(el, "trade", out var d)) yield break;
        yield return new TradeTick(
            Micros(d),
            CryptoConvert.D(d, "price"),
            CryptoConvert.ToSize(CryptoConvert.D(d, "amount"), scale),
            CryptoConvert.D(d, "type") == 1 ? AggressorSide.Sell : AggressorSide.Buy);
    }

    /// <summary><c>{"data":{"ohlc":[{timestamp (s), open, high, low, close, volume}, …]}}</c>, oldest first.</summary>
    internal static IReadOnlyList<Bar> ParseOhlc(JsonElement root, double scale)
    {
        var bars = new List<Bar>();
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("ohlc", out var rows)
            || rows.ValueKind != JsonValueKind.Array)
            return bars;

        foreach (var row in rows.EnumerateArray())
            bars.Add(new Bar(
                CryptoConvert.SecondsUtc(row.TryGetProperty("timestamp", out var t) ? CryptoConvert.L(t) : 0),
                CryptoConvert.D(row, "open"), CryptoConvert.D(row, "high"), CryptoConvert.D(row, "low"), CryptoConvert.D(row, "close"),
                CryptoConvert.ToSize(CryptoConvert.D(row, "volume"), scale)));
        return bars;
    }
}
