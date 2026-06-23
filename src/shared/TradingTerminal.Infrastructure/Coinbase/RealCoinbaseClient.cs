using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Coinbase;

/// <summary>
/// Coinbase market-data client over the <b>public</b> Advanced Trade WebSocket + Exchange REST
/// endpoints (no API key, no account). L1 ← <c>ticker</c>, L2 ← <c>level2</c> (snapshot + updates,
/// reconstructed via <see cref="L2OrderBook"/>), trades ← <c>market_trades</c>, bars ← <c>candles</c>
/// live and REST <c>/products/{id}/candles</c> for history. Data-only.
/// </summary>
internal sealed class RealCoinbaseClient : IBrokerClient
{
    private readonly ILogger<RealCoinbaseClient> _logger;
    private readonly CoinbaseOptions _options;
    private readonly HttpClient _http = new();
    private readonly System.Reactive.Subjects.BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);

    public RealCoinbaseClient(ILogger<RealCoinbaseClient> logger, IOptions<CoinbaseOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        // Coinbase REST rejects requests without a User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DaxAlgoTerminal/1.0");
    }

    public BrokerKind Kind => BrokerKind.Coinbase;
    public IObservable<ConnectionState> ConnectionState => System.Reactive.Linq.Observable.AsObservable(_state);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Connecting);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await _http.GetAsync($"{_options.RestBaseUrl}/time", cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            _logger.LogInformation("Coinbase connected — public market data at {Host} (no credentials).", _options.WsBaseUrl);
            _state.OnNext(Core.Domain.ConnectionState.Connected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { _state.OnNext(Core.Domain.ConnectionState.Disconnected); throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coinbase connect failed reaching {Host}.", _options.RestBaseUrl);
            _state.OnNext(Core.Domain.ConnectionState.Failed);
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<TradableInstrument> list = _options.Instruments
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().ToUpperInvariant()).Distinct()
            .Select(s => new TradableInstrument($"{s}  —  Coinbase", "Crypto (Coinbase)",
                new Contract(s, "CRYPTO", "COINBASE", QuoteOf(s), PrimaryExchange: string.Empty), BrokerKind.Coinbase))
            .ToList();
        return Task.FromResult(list);
    }

    public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        var id = contract.Symbol.Trim().ToUpperInvariant();
        var url = $"{_options.RestBaseUrl}/products/{id}/candles?granularity={Granularity(barSize)}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        var bars = new List<Bar>();
        // Exchange REST candles: [ time(sec), low, high, open, close, volume ], newest first.
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var row in doc.RootElement.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 6)
                    bars.Add(new Bar(
                        DateTimeOffset.FromUnixTimeSeconds(row[0].GetInt64()).UtcDateTime,
                        CryptoConvert.D(row[3]), CryptoConvert.D(row[2]), CryptoConvert.D(row[1]), CryptoConvert.D(row[4]),
                        CryptoConvert.ToSize(CryptoConvert.D(row[5]), _options.SizeScale)));
        bars.Reverse();
        return bars;
    }

    public IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, CancellationToken ct = default) =>
        Stream("candles", contract, el => ParseCandles(el, _options.SizeScale), ct);

    public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
        Stream("ticker", contract, el => ParseTicker(el, _options.SizeScale), ct);

    public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default)
    {
        var book = new L2OrderBook();
        var depth = Math.Max(levels, _options.DepthLevels);
        return Stream("level2", contract, el => ParseDepth(el, book, depth, _options.SizeScale), ct);
    }

    public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        Stream("market_trades", contract, el => ParseTrades(el, _options.SizeScale), ct);

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync().ConfigureAwait(false); } catch { }
        _http.Dispose();
        _state.Dispose();
    }

    private IAsyncEnumerable<T> Stream<T>(string channel, Contract contract, Func<JsonElement, IEnumerable<T>> parse, CancellationToken ct)
    {
        var id = contract.Symbol.Trim().ToUpperInvariant();
        var sub = $"{{\"type\":\"subscribe\",\"product_ids\":[\"{id}\"],\"channel\":\"{channel}\"}}";
        return CryptoStream.StreamAsync(_options.WsBaseUrl, sub, parse,
            _options.ReconnectInitialDelaySeconds, _options.ReconnectMaxDelaySeconds, _logger, "Coinbase", ct: ct);
    }

    // ── Parsers ─────────────────────────────────────────────────────────────────────────────────
    private static bool TryEvents(JsonElement el, out JsonElement events)
    {
        events = default;
        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty("events", out events) && events.ValueKind == JsonValueKind.Array;
    }

    internal static IEnumerable<Tick> ParseTicker(JsonElement el, double scale)
    {
        if (!TryEvents(el, out var events)) yield break;
        foreach (var ev in events.EnumerateArray())
            if (ev.TryGetProperty("tickers", out var tickers) && tickers.ValueKind == JsonValueKind.Array)
                foreach (var t in tickers.EnumerateArray())
                    yield return new Tick(DateTime.UtcNow,
                        CryptoConvert.D(t, "best_bid"), CryptoConvert.D(t, "best_ask"),
                        CryptoConvert.ToSize(CryptoConvert.D(t, "best_bid_quantity"), scale),
                        CryptoConvert.ToSize(CryptoConvert.D(t, "best_ask_quantity"), scale));
    }

    internal static IEnumerable<TradeTick> ParseTrades(JsonElement el, double scale)
    {
        if (!TryEvents(el, out var events)) yield break;
        foreach (var ev in events.EnumerateArray())
            if (ev.TryGetProperty("trades", out var trades) && trades.ValueKind == JsonValueKind.Array)
                foreach (var t in trades.EnumerateArray())
                {
                    var time = t.TryGetProperty("time", out var ts) && ts.TryGetDateTime(out var dt)
                        ? dt.ToUniversalTime() : DateTime.UtcNow;
                    var side = t.TryGetProperty("side", out var s) &&
                        string.Equals(s.GetString(), "SELL", StringComparison.OrdinalIgnoreCase)
                        ? AggressorSide.Sell : AggressorSide.Buy;
                    yield return new TradeTick(time, CryptoConvert.D(t, "price"), CryptoConvert.ToSize(CryptoConvert.D(t, "size"), scale), side);
                }
    }

    internal static IEnumerable<Bar> ParseCandles(JsonElement el, double scale)
    {
        if (!TryEvents(el, out var events)) yield break;
        foreach (var ev in events.EnumerateArray())
            if (ev.TryGetProperty("candles", out var candles) && candles.ValueKind == JsonValueKind.Array)
                foreach (var c in candles.EnumerateArray())
                {
                    var start = c.TryGetProperty("start", out var st) && long.TryParse(st.GetString(), out var sec)
                        ? DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime : DateTime.UtcNow;
                    yield return new Bar(start,
                        CryptoConvert.D(c, "open"), CryptoConvert.D(c, "high"), CryptoConvert.D(c, "low"), CryptoConvert.D(c, "close"),
                        CryptoConvert.ToSize(CryptoConvert.D(c, "volume"), scale));
                }
    }

    private static IEnumerable<DepthSnapshot> ParseDepth(JsonElement el, L2OrderBook book, int levels, double scale)
    {
        if (!TryEvents(el, out var events)) yield break;
        var changed = false;
        foreach (var ev in events.EnumerateArray())
        {
            if (ev.TryGetProperty("type", out var ty) && string.Equals(ty.GetString(), "snapshot", StringComparison.OrdinalIgnoreCase))
                book.Clear();
            if (!ev.TryGetProperty("updates", out var updates) || updates.ValueKind != JsonValueKind.Array) continue;
            foreach (var u in updates.EnumerateArray())
            {
                var isBid = u.TryGetProperty("side", out var sd) &&
                    string.Equals(sd.GetString(), "bid", StringComparison.OrdinalIgnoreCase);
                book.Apply(isBid, CryptoConvert.D(u, "price_level"), CryptoConvert.D(u, "new_quantity"));
                changed = true;
            }
        }
        if (changed) yield return book.Snapshot(levels, scale);
    }

    private static string Granularity(BarSize size) => size switch
    {
        BarSize.OneMinute => "60",
        BarSize.ThreeMinutes => "300",   // Coinbase has no 3m; nearest supported
        BarSize.FiveMinutes => "300",
        BarSize.FifteenMinutes => "900",
        BarSize.OneHour => "3600",
        BarSize.OneDay => "86400",
        _ => "60",
    };

    private static string QuoteOf(string id)
    {
        var dash = id.IndexOf('-');
        return dash > 0 && dash < id.Length - 1 ? id[(dash + 1)..] : "USD";
    }
}
