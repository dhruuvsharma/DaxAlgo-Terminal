using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Bybit;

/// <summary>
/// Bybit market-data client over the <b>public</b> v5 WebSocket + REST endpoints (no API key, no
/// account). L1 ← <c>tickers</c>, L2 ← <c>orderbook.{depth}</c> (snapshot + deltas, reconstructed via
/// <see cref="L2OrderBook"/>), trades ← <c>publicTrade</c>, bars ← <c>kline</c> live and REST
/// <c>/v5/market/kline</c> for history. Data-only.
/// </summary>
internal sealed class RealBybitClient : IBrokerClient
{
    private readonly ILogger<RealBybitClient> _logger;
    private readonly BybitOptions _options;
    private readonly HttpClient _http = new();
    private readonly System.Reactive.Subjects.BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);

    public RealBybitClient(ILogger<RealBybitClient> logger, IOptions<BybitOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public BrokerKind Kind => BrokerKind.Bybit;
    public IObservable<ConnectionState> ConnectionState => System.Reactive.Linq.Observable.AsObservable(_state);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Connecting);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await _http.GetAsync($"{_options.RestBaseUrl}/v5/market/time", cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            _logger.LogInformation("Bybit connected — public market data at {Host} (no credentials).", _options.WsBaseUrl);
            _state.OnNext(Core.Domain.ConnectionState.Connected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { _state.OnNext(Core.Domain.ConnectionState.Disconnected); throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bybit connect failed reaching {Host}.", _options.RestBaseUrl);
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
            .Select(s => new TradableInstrument($"{s}  —  Bybit", "Crypto (Bybit)",
                new Contract(s, "CRYPTO", "BYBIT", QuoteOf(s), PrimaryExchange: string.Empty), BrokerKind.Bybit))
            .ToList();
        return Task.FromResult(list);
    }

    public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        var symbol = contract.Symbol.Trim().ToUpperInvariant();
        var interval = MapInterval(barSize);
        var step = barSize.ToTimeSpan();
        var limit = Math.Clamp((int)Math.Ceiling(duration / step), 1, 1000);
        var url = $"{_options.RestBaseUrl}/v5/market/kline?category={_options.Category}&symbol={symbol}&interval={interval}&limit={limit}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        var bars = new List<Bar>();
        if (doc.RootElement.TryGetProperty("result", out var result) &&
            result.TryGetProperty("list", out var rows) && rows.ValueKind == JsonValueKind.Array)
            foreach (var row in rows.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 6)
                    bars.Add(new Bar(
                        DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(row[0].GetString() ?? "0")).UtcDateTime,
                        CryptoConvert.D(row[1]), CryptoConvert.D(row[2]), CryptoConvert.D(row[3]), CryptoConvert.D(row[4]),
                        CryptoConvert.ToSize(CryptoConvert.D(row[5]), _options.SizeScale)));
        bars.Reverse(); // Bybit returns newest-first
        return bars;
    }

    public IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, CancellationToken ct = default) =>
        Stream($"kline.{MapInterval(barSize)}.{Sym(contract)}", el => ParseKline(el, _options.SizeScale), ct);

    public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
        Stream($"tickers.{Sym(contract)}", el => ParseTicker(el, _options.SizeScale), ct);

    public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default)
    {
        var book = new L2OrderBook();
        return Stream($"orderbook.{_options.DepthLevels}.{Sym(contract)}", el => ParseDepth(el, book, levels, _options.SizeScale), ct);
    }

    public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        Stream($"publicTrade.{Sym(contract)}", el => ParseTrades(el, _options.SizeScale), ct);

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync().ConfigureAwait(false); } catch { }
        _http.Dispose();
        _state.Dispose();
    }

    private IAsyncEnumerable<T> Stream<T>(string topic, Func<JsonElement, IEnumerable<T>> parse, CancellationToken ct)
    {
        var sub = $"{{\"op\":\"subscribe\",\"args\":[\"{topic}\"]}}";
        return CryptoStream.StreamAsync(_options.WsBaseUrl, sub, parse,
            _options.ReconnectInitialDelaySeconds, _options.ReconnectMaxDelaySeconds, _logger, "Bybit",
            pingJson: "{\"op\":\"ping\"}", pingIntervalSeconds: 15, ct: ct);
    }

    // ── Parsers ─────────────────────────────────────────────────────────────────────────────────
    internal static IEnumerable<Bar> ParseKline(JsonElement el, double scale)
    {
        if (!el.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) yield break;
        foreach (var k in data.EnumerateArray())
            yield return new Bar(
                DateTimeOffset.FromUnixTimeMilliseconds((long)CryptoConvert.MsToTicksUtc(k, "start")).UtcDateTime,
                CryptoConvert.D(k, "open"), CryptoConvert.D(k, "high"), CryptoConvert.D(k, "low"), CryptoConvert.D(k, "close"),
                CryptoConvert.ToSize(CryptoConvert.D(k, "volume"), scale));
    }

    internal static IEnumerable<Tick> ParseTicker(JsonElement el, double scale)
    {
        if (!el.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) yield break;
        yield return new Tick(DateTime.UtcNow,
            CryptoConvert.D(d, "bid1Price"), CryptoConvert.D(d, "ask1Price"),
            CryptoConvert.ToSize(CryptoConvert.D(d, "bid1Size"), scale),
            CryptoConvert.ToSize(CryptoConvert.D(d, "ask1Size"), scale));
    }

    internal static IEnumerable<TradeTick> ParseTrades(JsonElement el, double scale)
    {
        if (!el.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) yield break;
        foreach (var t in data.EnumerateArray())
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(CryptoConvert.MsToTicksUtc(t, "T")).UtcDateTime;
            var side = t.TryGetProperty("S", out var s) && s.GetString() == "Sell" ? AggressorSide.Sell : AggressorSide.Buy;
            yield return new TradeTick(time, CryptoConvert.D(t, "p"), CryptoConvert.ToSize(CryptoConvert.D(t, "v"), scale), side);
        }
    }

    private static IEnumerable<DepthSnapshot> ParseDepth(JsonElement el, L2OrderBook book, int levels, double scale)
    {
        if (!el.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) yield break;
        var type = el.TryGetProperty("type", out var t) ? t.GetString() : "delta";
        if (type == "snapshot") book.Clear();
        ApplySide(book, d, "b", isBid: true);
        ApplySide(book, d, "a", isBid: false);
        yield return book.Snapshot(levels, scale);
    }

    private static void ApplySide(L2OrderBook book, JsonElement data, string name, bool isBid)
    {
        if (!data.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var lvl in arr.EnumerateArray())
            if (lvl.ValueKind == JsonValueKind.Array && lvl.GetArrayLength() >= 2)
                book.Apply(isBid, CryptoConvert.D(lvl[0]), CryptoConvert.D(lvl[1]));
    }

    private static string Sym(Contract contract) => contract.Symbol.Trim().ToUpperInvariant();

    internal static string MapInterval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1",
        BarSize.ThreeMinutes => "3",
        BarSize.FiveMinutes => "5",
        BarSize.FifteenMinutes => "15",
        BarSize.OneHour => "60",
        BarSize.OneDay => "D",
        _ => "1",
    };

    private static string QuoteOf(string symbol)
    {
        foreach (var q in new[] { "USDT", "USDC", "USD", "BTC", "ETH", "EUR" })
            if (symbol.EndsWith(q, StringComparison.Ordinal)) return q;
        return "USDT";
    }
}
