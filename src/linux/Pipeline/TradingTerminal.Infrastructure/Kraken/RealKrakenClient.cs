using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Kraken;

/// <summary>
/// Kraken market-data client over the <b>public</b> WebSocket v2 + REST endpoints (no API key, no
/// account). L1 ← <c>ticker</c>, L2 ← <c>book</c> (snapshot + updates, reconstructed via
/// <see cref="L2OrderBook"/>), trades ← <c>trade</c>, bars ← <c>ohlc</c> live and REST
/// <c>/0/public/OHLC</c> for history. Symbols use slash form (BTC/USD) on the WS; REST history maps
/// them to Kraken altname pairs. Data-only.
/// </summary>
internal sealed class RealKrakenClient : IBrokerClient
{
    private readonly ILogger<RealKrakenClient> _logger;
    private readonly KrakenOptions _options;
    private readonly HttpClient _http = new();
    private readonly System.Reactive.Subjects.BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);

    public RealKrakenClient(ILogger<RealKrakenClient> logger, IOptions<KrakenOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public BrokerKind Kind => BrokerKind.Kraken;
    public IObservable<ConnectionState> ConnectionState => System.Reactive.Linq.Observable.AsObservable(_state);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Connecting);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await _http.GetAsync($"{_options.RestBaseUrl}/0/public/Time", cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            _logger.LogInformation("Kraken connected — public market data at {Host} (no credentials).", _options.WsBaseUrl);
            _state.OnNext(Core.Domain.ConnectionState.Connected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { _state.OnNext(Core.Domain.ConnectionState.Disconnected); throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kraken connect failed reaching {Host}.", _options.RestBaseUrl);
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
            .Select(s => new TradableInstrument($"{s}  —  Kraken", "Crypto (Kraken)",
                new Contract(s, "CRYPTO", "KRAKEN", QuoteOf(s), PrimaryExchange: string.Empty), BrokerKind.Kraken))
            .ToList();
        return Task.FromResult(list);
    }

    public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        var pair = RestPair(contract.Symbol);
        var url = $"{_options.RestBaseUrl}/0/public/OHLC?pair={pair}&interval={MapInterval(barSize)}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        var bars = new List<Bar>();
        if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in result.EnumerateObject())
            {
                if (prop.Name == "last" || prop.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var row in prop.Value.EnumerateArray())
                    if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 7)
                        bars.Add(new Bar(
                            DateTimeOffset.FromUnixTimeSeconds(row[0].GetInt64()).UtcDateTime,
                            CryptoConvert.D(row[1]), CryptoConvert.D(row[2]), CryptoConvert.D(row[3]), CryptoConvert.D(row[4]),
                            CryptoConvert.ToSize(CryptoConvert.D(row[6]), _options.SizeScale))); // [6]=volume, [5]=vwap
                break; // only the one pair series
            }
        }
        return bars; // Kraken returns oldest-first
    }

    public IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, CancellationToken ct = default) =>
        Stream($"{{\"method\":\"subscribe\",\"params\":{{\"channel\":\"ohlc\",\"symbol\":[\"{Sym(contract)}\"],\"interval\":{MapInterval(barSize)}}}}}",
            el => ParseOhlc(el, _options.SizeScale), ct);

    public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
        Stream($"{{\"method\":\"subscribe\",\"params\":{{\"channel\":\"ticker\",\"symbol\":[\"{Sym(contract)}\"]}}}}",
            el => ParseTicker(el, _options.SizeScale), ct);

    public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default)
    {
        var book = new L2OrderBook();
        var depth = KrakenDepth(_options.DepthLevels);
        return Stream($"{{\"method\":\"subscribe\",\"params\":{{\"channel\":\"book\",\"symbol\":[\"{Sym(contract)}\"],\"depth\":{depth}}}}}",
            el => ParseBook(el, book, levels, _options.SizeScale), ct);
    }

    public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        Stream($"{{\"method\":\"subscribe\",\"params\":{{\"channel\":\"trade\",\"symbol\":[\"{Sym(contract)}\"]}}}}",
            el => ParseTrades(el, _options.SizeScale), ct);

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync().ConfigureAwait(false); } catch { }
        _http.Dispose();
        _state.Dispose();
    }

    private IAsyncEnumerable<T> Stream<T>(string subscribeJson, Func<JsonElement, IEnumerable<T>> parse, CancellationToken ct) =>
        CryptoStream.StreamAsync(_options.WsBaseUrl, subscribeJson, parse,
            _options.ReconnectInitialDelaySeconds, _options.ReconnectMaxDelaySeconds, _logger, "Kraken",
            pingJson: "{\"method\":\"ping\"}", pingIntervalSeconds: 20, ct: ct);

    // ── Parsers ─────────────────────────────────────────────────────────────────────────────────
    private static bool TryData(JsonElement el, string channel, out JsonElement data)
    {
        data = default;
        return el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("channel", out var ch) && ch.GetString() == channel
            && el.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Array;
    }

    internal static IEnumerable<Tick> ParseTicker(JsonElement el, double scale)
    {
        if (!TryData(el, "ticker", out var data)) yield break;
        foreach (var t in data.EnumerateArray())
            yield return new Tick(DateTime.UtcNow,
                CryptoConvert.D(t, "bid"), CryptoConvert.D(t, "ask"),
                CryptoConvert.ToSize(CryptoConvert.D(t, "bid_qty"), scale),
                CryptoConvert.ToSize(CryptoConvert.D(t, "ask_qty"), scale));
    }

    internal static IEnumerable<TradeTick> ParseTrades(JsonElement el, double scale)
    {
        if (!TryData(el, "trade", out var data)) yield break;
        foreach (var t in data.EnumerateArray())
        {
            var time = t.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var dt)
                ? dt.ToUniversalTime() : DateTime.UtcNow;
            var side = t.TryGetProperty("side", out var s) &&
                string.Equals(s.GetString(), "sell", StringComparison.OrdinalIgnoreCase) ? AggressorSide.Sell : AggressorSide.Buy;
            yield return new TradeTick(time, CryptoConvert.D(t, "price"), CryptoConvert.ToSize(CryptoConvert.D(t, "qty"), scale), side);
        }
    }

    internal static IEnumerable<Bar> ParseOhlc(JsonElement el, double scale)
    {
        if (!TryData(el, "ohlc", out var data)) yield break;
        foreach (var c in data.EnumerateArray())
        {
            var time = c.TryGetProperty("interval_begin", out var ib) && ib.TryGetDateTime(out var dt)
                ? dt.ToUniversalTime() : DateTime.UtcNow;
            yield return new Bar(time,
                CryptoConvert.D(c, "open"), CryptoConvert.D(c, "high"), CryptoConvert.D(c, "low"), CryptoConvert.D(c, "close"),
                CryptoConvert.ToSize(CryptoConvert.D(c, "volume"), scale));
        }
    }

    private static IEnumerable<DepthSnapshot> ParseBook(JsonElement el, L2OrderBook book, int levels, double scale)
    {
        if (!TryData(el, "book", out var data)) yield break;
        var type = el.TryGetProperty("type", out var ty) ? ty.GetString() : "update";
        if (type == "snapshot") book.Clear();
        foreach (var b in data.EnumerateArray())
        {
            ApplySide(book, b, "bids", isBid: true);
            ApplySide(book, b, "asks", isBid: false);
        }
        yield return book.Snapshot(levels, scale);
    }

    private static void ApplySide(L2OrderBook book, JsonElement data, string name, bool isBid)
    {
        if (!data.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var lvl in arr.EnumerateArray())
            if (lvl.ValueKind == JsonValueKind.Object)
                book.Apply(isBid, CryptoConvert.D(lvl, "price"), CryptoConvert.D(lvl, "qty"));
    }

    private static string Sym(Contract contract) => contract.Symbol.Trim().ToUpperInvariant();

    /// <summary>Kraken book depth must be one of 10/25/100/500/1000.</summary>
    private static int KrakenDepth(int requested) => requested switch
    {
        <= 10 => 10, <= 25 => 25, <= 100 => 100, <= 500 => 500, _ => 1000,
    };

    internal static int MapInterval(BarSize size) => size switch
    {
        BarSize.OneMinute => 1,
        BarSize.ThreeMinutes => 5,   // Kraken has no 3m; nearest supported
        BarSize.FiveMinutes => 5,
        BarSize.FifteenMinutes => 15,
        BarSize.OneHour => 60,
        BarSize.OneDay => 1440,
        _ => 1,
    };

    /// <summary>Map a slash pair (BTC/USD) to a Kraken REST altname pair (XBTUSD). Best-effort — Kraken's
    /// naming is idiosyncratic (XBT for BTC, XDG for DOGE); unusual pairs may need a fuller map.</summary>
    private static string RestPair(string symbol)
    {
        var parts = symbol.Trim().ToUpperInvariant().Split('/');
        if (parts.Length != 2) return symbol.Replace("/", "");
        var baseAsset = parts[0] switch { "BTC" => "XBT", "DOGE" => "XDG", var b => b };
        return $"{baseAsset}{parts[1]}";
    }

    private static string QuoteOf(string symbol)
    {
        var slash = symbol.IndexOf('/');
        return slash > 0 && slash < symbol.Length - 1 ? symbol[(slash + 1)..] : "USD";
    }
}
