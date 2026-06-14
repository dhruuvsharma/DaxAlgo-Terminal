using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Okx;

/// <summary>
/// OKX market-data client over the <b>public</b> v5 WebSocket + REST endpoints (no API key, no
/// account). L1 ← <c>tickers</c>, L2 ← <c>books5</c> (full 5-level snapshots — no reconstruction),
/// trades ← <c>trades</c>, bars ← <c>candle&lt;tf&gt;</c> live and REST <c>/api/v5/market/candles</c> for
/// history. Data-only.
/// </summary>
internal sealed class RealOkxClient : IBrokerClient
{
    private readonly ILogger<RealOkxClient> _logger;
    private readonly OkxOptions _options;
    private readonly HttpClient _http = new();
    private readonly System.Reactive.Subjects.BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);

    public RealOkxClient(ILogger<RealOkxClient> logger, IOptions<OkxOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public BrokerKind Kind => BrokerKind.Okx;
    public IObservable<ConnectionState> ConnectionState => System.Reactive.Linq.Observable.AsObservable(_state);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Connecting);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await _http.GetAsync($"{_options.RestBaseUrl}/api/v5/public/time", cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            _logger.LogInformation("OKX connected — public market data at {Host} (no credentials).", _options.WsBaseUrl);
            _state.OnNext(Core.Domain.ConnectionState.Connected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { _state.OnNext(Core.Domain.ConnectionState.Disconnected); throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OKX connect failed reaching {Host}.", _options.RestBaseUrl);
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
            .Select(s => new TradableInstrument($"{s}  —  OKX", "Crypto (OKX)",
                new Contract(s, "CRYPTO", "OKX", QuoteOf(s), PrimaryExchange: string.Empty), BrokerKind.Okx))
            .ToList();
        return Task.FromResult(list);
    }

    public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        var instId = contract.Symbol.Trim().ToUpperInvariant();
        var bar = MapInterval(barSize);
        var step = barSize.ToTimeSpan();
        var limit = Math.Clamp((int)Math.Ceiling(duration / step), 1, 300);
        var url = $"{_options.RestBaseUrl}/api/v5/market/candles?instId={instId}&bar={bar}&limit={limit}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        var bars = new List<Bar>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var row in data.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 6)
                    bars.Add(new Bar(
                        DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(row[0].GetString() ?? "0")).UtcDateTime,
                        CryptoConvert.D(row[1]), CryptoConvert.D(row[2]), CryptoConvert.D(row[3]), CryptoConvert.D(row[4]),
                        CryptoConvert.ToSize(CryptoConvert.D(row[5]), _options.SizeScale)));
        bars.Reverse(); // OKX returns newest-first
        return bars;
    }

    public IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, CancellationToken ct = default) =>
        Stream($"candle{MapInterval(barSize)}", contract, el => ParseCandles(el, _options.SizeScale), ct);

    public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
        Stream("tickers", contract, el => ParseTickers(el, _options.SizeScale), ct);

    public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default) =>
        Stream("books5", contract, el => ParseBooks5(el, _options.SizeScale), ct);

    public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        Stream("trades", contract, el => ParseTrades(el, _options.SizeScale), ct);

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync().ConfigureAwait(false); } catch { }
        _http.Dispose();
        _state.Dispose();
    }

    private IAsyncEnumerable<T> Stream<T>(string channel, Contract contract, Func<JsonElement, IEnumerable<T>> parse, CancellationToken ct)
    {
        var instId = contract.Symbol.Trim().ToUpperInvariant();
        var sub = $"{{\"op\":\"subscribe\",\"args\":[{{\"channel\":\"{channel}\",\"instId\":\"{instId}\"}}]}}";
        return CryptoStream.StreamAsync(_options.WsBaseUrl, sub, parse,
            _options.ReconnectInitialDelaySeconds, _options.ReconnectMaxDelaySeconds, _logger, "OKX",
            pingJson: "ping", pingIntervalSeconds: 15, ct: ct);
    }

    // ── Parsers ─────────────────────────────────────────────────────────────────────────────────
    private static bool TryData(JsonElement el, out JsonElement data)
    {
        data = default;
        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Array;
    }

    internal static IEnumerable<Tick> ParseTickers(JsonElement el, double scale)
    {
        if (!TryData(el, out var data)) yield break;
        foreach (var t in data.EnumerateArray())
            yield return new Tick(DateTime.UtcNow,
                CryptoConvert.D(t, "bidPx"), CryptoConvert.D(t, "askPx"),
                CryptoConvert.ToSize(CryptoConvert.D(t, "bidSz"), scale),
                CryptoConvert.ToSize(CryptoConvert.D(t, "askSz"), scale));
    }

    internal static IEnumerable<DepthSnapshot> ParseBooks5(JsonElement el, double scale)
    {
        if (!TryData(el, out var data)) yield break;
        foreach (var b in data.EnumerateArray())
        {
            var ts = b.TryGetProperty("ts", out var tse) && long.TryParse(tse.GetString(), out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UtcNow;
            yield return new DepthSnapshot(ts,
                ReadLevels(b, "bids", scale), ReadLevels(b, "asks", scale));
        }
    }

    internal static IEnumerable<TradeTick> ParseTrades(JsonElement el, double scale)
    {
        if (!TryData(el, out var data)) yield break;
        foreach (var t in data.EnumerateArray())
        {
            var time = t.TryGetProperty("ts", out var tse) && long.TryParse(tse.GetString(), out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UtcNow;
            var side = t.TryGetProperty("side", out var s) && s.GetString() == "sell" ? AggressorSide.Sell : AggressorSide.Buy;
            yield return new TradeTick(time, CryptoConvert.D(t, "px"), CryptoConvert.ToSize(CryptoConvert.D(t, "sz"), scale), side);
        }
    }

    internal static IEnumerable<Bar> ParseCandles(JsonElement el, double scale)
    {
        if (!TryData(el, out var data)) yield break;
        foreach (var row in data.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 6)
                yield return new Bar(
                    DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(row[0].GetString() ?? "0")).UtcDateTime,
                    CryptoConvert.D(row[1]), CryptoConvert.D(row[2]), CryptoConvert.D(row[3]), CryptoConvert.D(row[4]),
                    CryptoConvert.ToSize(CryptoConvert.D(row[5]), scale));
    }

    private static IReadOnlyList<DepthLevel> ReadLevels(JsonElement obj, string name, double scale)
    {
        var levels = new List<DepthLevel>();
        if (obj.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var lvl in arr.EnumerateArray())
                if (lvl.ValueKind == JsonValueKind.Array && lvl.GetArrayLength() >= 2)
                    levels.Add(new DepthLevel(CryptoConvert.D(lvl[0]), CryptoConvert.ToSize(CryptoConvert.D(lvl[1]), scale)));
        return levels;
    }

    internal static string MapInterval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1m",
        BarSize.ThreeMinutes => "3m",
        BarSize.FiveMinutes => "5m",
        BarSize.FifteenMinutes => "15m",
        BarSize.OneHour => "1H",
        BarSize.OneDay => "1D",
        _ => "1m",
    };

    private static string QuoteOf(string instId)
    {
        var dash = instId.IndexOf('-');
        return dash > 0 && dash < instId.Length - 1 ? instId[(dash + 1)..] : "USDT";
    }
}
