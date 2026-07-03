using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Threading;

namespace TradingTerminal.Infrastructure.LondonStrategicEdge;

/// <summary>
/// London Strategic Edge market-data client — free multi-asset L1 ticks + historical OHLCV over
/// a hand-rolled WebSocket + REST pair (no SDK, same shape as <c>RealIronBeamClient</c> /
/// <c>RealBinanceClient</c>). The connection model is one socket for everything:
/// <list type="number">
/// <item>Open <c>wss://data-ws.londonstrategicedge.com</c>.</item>
/// <item>Send <c>{"action":"auth","api_key":...}</c>; wait for <c>{"type":"authenticated"}</c>.</item>
/// <item>Subscribe per symbol with <c>{"action":"subscribe","symbol":...}</c>; every market update
/// arrives as a <c>{"type":"tick", symbol, price, bid, ask, volume, ts, replay}</c> message.</item>
/// <item>Keepalive <c>{"action":"ping"}</c> every <see cref="LondonStrategicEdgeOptions.PingIntervalSeconds"/>
/// (server idle timeout is 600s).</item>
/// </list>
/// One background read pump owns the socket, fanning ticks out to per-symbol channels; on a drop it
/// reconnects with exponential backoff, re-auths, and re-issues every active subscription. Fatal
/// protocol errors (<c>INVALID_KEY</c> / <c>MISSING_KEY</c> / <c>QUOTA_EXCEEDED</c> — the 50 GB/month
/// free-tier quota) stop the pump instead of retry-looping, surfacing loudly as <c>Failed</c>.
///
/// History comes from a PostgREST-style REST API: shared <c>x_candles_{tf}</c> tables for 5m+
/// timeframes (filtered <c>symbol=eq.{symbol}</c>), per-symbol <c>candles_{slug}</c> tables for 1m
/// (with a <c>d_candles_{slug}</c> fallback on 404). The feed has no L2 depth, and the tick stream
/// is not yet verified to carry true trade prints, so <see cref="SubscribeDepthAsync"/> and
/// <see cref="SubscribeTradesAsync"/> throw <see cref="NotSupportedException"/> (strategies fall
/// back to Synthetic-L1 bootstrap, correctly badged). Data-only — the provider has no order path.
/// </summary>
internal sealed class RealLondonStrategicEdgeClient : IBrokerClient
{
    private readonly ILogger<RealLondonStrategicEdgeClient> _logger;
    private readonly LondonStrategicEdgeOptions _options;
    private readonly HttpClient _http = new();
    private readonly BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);

    // Pump plumbing — recreated per Connect; guarded by _sync. _ws is the live socket the send
    // helpers write to (null while down); writes are serialized by _sendLock.
    private readonly object _sync = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

    // Active tick subscriptions keyed by LSE symbol, so a reconnect can re-subscribe and the pump
    // can fan incoming ticks to the matching consumer channels.
    private readonly ConcurrentDictionary<string, Subscription> _subs = new();

    private int _loggedUnknownTick;

    public RealLondonStrategicEdgeClient(
        ILogger<RealLondonStrategicEdgeClient> logger,
        IOptions<LondonStrategicEdgeOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DaxAlgoTerminal/1.0");
    }

    public BrokerKind Kind => BrokerKind.LondonStrategicEdge;

    public IObservable<ConnectionState> ConnectionState => _state.AsObservable();

    // ── Connect / disconnect ────────────────────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogError("London Strategic Edge connect failed — no API key configured.");
            _state.OnNext(Core.Domain.ConnectionState.Failed);
            return;
        }

        _state.OnNext(Core.Domain.ConnectionState.Connecting);

        // The pump owns the socket for the session; the first connect+auth outcome is reported
        // back through this TCS so ConnectAsync resolves once we're authenticated (or failed).
        var firstAuth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pumpCts = new CancellationTokenSource();
        var pumpTask = RunPumpAsync(firstAuth, pumpCts.Token);
        lock (_sync)
        {
            _pumpCts = pumpCts;
            _pumpTask = pumpTask;
        }

        try
        {
            using var reg = ct.Register(() => firstAuth.TrySetCanceled(ct));
            await firstAuth.Task.ConfigureAwait(false);
            _logger.LogInformation("London Strategic Edge connected — {Ws}.", _options.WsUrl);
            _state.OnNext(Core.Domain.ConnectionState.Connected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "London Strategic Edge connect failed reaching {Ws}.", _options.WsUrl);
            _state.OnNext(Core.Domain.ConnectionState.Failed);
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? pumpCts;
        Task? pumpTask;
        lock (_sync)
        {
            pumpCts = _pumpCts;
            pumpTask = _pumpTask;
            _pumpCts = null;
            _pumpTask = null;
        }

        if (pumpCts is not null)
        {
            try { await pumpCts.CancelAsync().ConfigureAwait(false); } catch { /* swallow */ }
        }
        if (pumpTask is not null)
        {
            try { await pumpTask.ConfigureAwait(false); } catch { /* swallow */ }
        }
        pumpCts?.Dispose();

        foreach (var sub in _subs.Values)
            sub.Complete();

        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync().ConfigureAwait(false); } catch { /* swallow */ }
        _http.Dispose();
        _sendLock.Dispose();
        _state.Dispose();
    }

    // ── Instruments (keyless catalog) ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_options.CatalogUrl, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(bytes);
            return ParseCatalog(doc.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Cold catalog — the picker falls back to the curated SignalInstrumentCatalog.
            _logger.LogWarning(ex, "London Strategic Edge catalog fetch failed ({Url}).", _options.CatalogUrl);
            return Array.Empty<TradableInstrument>();
        }
    }

    /// <summary>Catalog entries are <c>{symbol, name, category}</c>; category is e.g. "Stocks" / "Forex".</summary>
    internal static IReadOnlyList<TradableInstrument> ParseCatalog(JsonElement root)
    {
        var list = new List<TradableInstrument>();
        if (root.ValueKind != JsonValueKind.Array) return list;

        foreach (var el in root.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var symbol = el.TryGetProperty("symbol", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(symbol)) continue;

            var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var category = el.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

            list.Add(new TradableInstrument(
                string.IsNullOrWhiteSpace(name) ? symbol! : $"{name}  ({symbol})",
                $"{(string.IsNullOrWhiteSpace(category) ? "Other" : category)} (LSE)",
                new Contract(symbol!, SecTypeOf(category), "LSE", QuoteCurrencyOf(symbol!), PrimaryExchange: string.Empty),
                BrokerKind.LondonStrategicEdge));
        }
        return list;
    }

    internal static string SecTypeOf(string? category) => category?.Trim().ToLowerInvariant() switch
    {
        "stocks" or "stock" => "STK",
        "etfs" or "etf" => "ETF",
        "forex" or "fx" => "CASH",
        "crypto" or "cryptocurrency" => "CRYPTO",
        "commodities" or "commodity" => "CMDTY",
        "indices" or "index" => "IND",
        _ => "STK",
    };

    /// <summary>"EUR/USD" → USD; bare symbols default to USD (LSE quotes everything in USD).</summary>
    internal static string QuoteCurrencyOf(string symbol)
    {
        var slash = symbol.LastIndexOf('/');
        if (slash >= 0 && slash < symbol.Length - 1)
        {
            var quote = symbol[(slash + 1)..].Trim();
            if (quote.Length is 3 or 4) return quote.ToUpperInvariant();
        }
        return "USD";
    }

    // ── Historical bars (PostgREST-style REST) ──────────────────────────────────────────────────

    public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        var symbol = ToLseSymbol(contract);
        var step = barSize.ToTimeSpan();
        var limit = Math.Clamp((int)Math.Ceiling(duration / step), 1, 5000);

        // Latest N bars: order desc + limit, then reverse to ascending for the callers.
        var query = $"order=timestamp.desc&limit={limit}";
        var tf = MapTimeframe(barSize);

        JsonDocument doc;
        if (tf != "1m")
        {
            doc = await RestGetAsync($"x_candles_{tf}?symbol=eq.{Uri.EscapeDataString(symbol)}&{query}", ct)
                .ConfigureAwait(false);
        }
        else
        {
            // 1m candles live in per-symbol tables; some symbols only have the "d_" variant.
            var slug = SymbolSlug(symbol);
            try
            {
                doc = await RestGetAsync($"candles_{slug}?{query}", ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                doc = await RestGetAsync($"d_candles_{slug}?{query}", ct).ConfigureAwait(false);
            }
        }

        using (doc)
        {
            var bars = ParseCandles(doc.RootElement);
            return bars.OrderBy(b => b.TimestampUtc).ToList();
        }
    }

    private async Task<JsonDocument> RestGetAsync(string pathAndQuery, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_options.RestBaseUrl.TrimEnd('/')}/{pathAndQuery}");
        req.Headers.Add("x-api-key", _options.ApiKey);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(bytes);
    }

    /// <summary>Rows are <c>{timestamp, open, high, low, close, volume}</c>; volume may be null/fractional.</summary>
    internal static IReadOnlyList<Bar> ParseCandles(JsonElement root)
    {
        var bars = new List<Bar>();
        if (root.ValueKind != JsonValueKind.Array) return bars;

        foreach (var row in root.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;

            var open = Number(row, "open");
            var high = Number(row, "high");
            var low = Number(row, "low");
            var close = Number(row, "close");
            if (open is null || high is null || low is null || close is null) continue;

            bars.Add(new Bar(
                Timestamp(row),
                open.Value, high.Value, low.Value, close.Value,
                (long)Math.Round(Number(row, "volume") ?? 0)));
        }
        return bars;
    }

    /// <summary>LSE timeframe token. 3m has no table — coarsest safe fallback is 1m (callers aggregate).</summary>
    internal static string MapTimeframe(BarSize size) => size switch
    {
        BarSize.OneMinute => "1m",
        BarSize.ThreeMinutes => "1m",
        BarSize.FiveMinutes => "5m",
        BarSize.FifteenMinutes => "15m",
        BarSize.OneHour => "1h",
        BarSize.OneDay => "1d",
        _ => "1m",
    };

    /// <summary>Mirrors the provider SDK's slug: lowercase, '/' '-' '.' → '_' (e.g. BTC/USD → btc_usd).</summary>
    internal static string SymbolSlug(string symbol) =>
        symbol.ToLowerInvariant().Replace('/', '_').Replace('-', '_').Replace('.', '_');

    // ── Streaming subscriptions ─────────────────────────────────────────────────────────────────

    public IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize, CancellationToken ct = default)
    {
        // Tick-primary: live bars are aggregated downstream from the L1 feed; the websocket has no
        // bar channel anyway.
        return EmptyAsync<Bar>(ct);
    }

    public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var symbol = ToLseSymbol(contract);
        var sub = new Subscription(symbol);

        if (_subs.TryGetValue(symbol, out var existing) && !ReferenceEquals(existing, sub))
            existing.Complete();
        _subs[symbol] = sub;

        try
        {
            // Best-effort — if the socket is down the pump re-subscribes everything on reconnect.
            await TrySendAsync(new { action = "subscribe", symbol }, ct).ConfigureAwait(false);

            await foreach (var tick in sub.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return tick;
        }
        finally
        {
            _subs.TryRemove(new KeyValuePair<string, Subscription>(symbol, sub));
            sub.Complete();
            await TrySendAsync(new { action = "unsubscribe", symbol }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "London Strategic Edge streams L1 ticks only — no order-book depth channel. Fall back to SubscribeTicksAsync.");

    public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "London Strategic Edge trade tape is not wired: the tick stream carries price+volume but has not been " +
            "verified to be true per-print trades (vs quote-derived updates). Strategies fall back to Synthetic-L1.");

    private static async IAsyncEnumerable<T> EmptyAsync<T>([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
#pragma warning disable CS0162 // unreachable — shape required to make this an async iterator
        yield return default!;
#pragma warning restore CS0162
    }

    // ── The single read pump (owns the WS, auths, fans out ticks, drives reconnect) ─────────────

    /// <summary>Error codes that must not reconnect-loop (bad key, or the monthly quota is gone).</summary>
    private static readonly string[] FatalErrorCodes = ["INVALID_KEY", "MISSING_KEY", "QUOTA_EXCEEDED"];

    private async Task RunPumpAsync(TaskCompletionSource<bool> firstAuth, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectInitialDelaySeconds));
        var maxDelay = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectMaxDelaySeconds));
        var everAuthenticated = false;

        while (!ct.IsCancellationRequested)
        {
            var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri(_options.WsUrl), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ws.Dispose();
                return;
            }
            catch (Exception ex)
            {
                ws.Dispose();
                if (!everAuthenticated)
                {
                    // First connect fails fast so the login form gets an immediate error pill.
                    firstAuth.TrySetException(ex);
                    return;
                }
                _logger.LogWarning(ex, "London Strategic Edge WS connect failed; retrying in {Delay}s.", delay.TotalSeconds);
                if (!await DelayAsync(delay, ct).ConfigureAwait(false)) return;
                delay = NextDelay(delay, maxDelay);
                continue;
            }

            lock (_sync) { _ws = ws; }
            var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task? pingTask = null;
            var authenticatedThisSession = false;
            var fatal = false;

            try
            {
                await TrySendAsync(new { action = "auth", api_key = _options.ApiKey }, ct).ConfigureAwait(false);

                while (!ct.IsCancellationRequested)
                {
                    var msg = await ReceiveMessageAsync(ws, ct).ConfigureAwait(false);
                    if (msg is null) break; // close / error → fall out and reconnect

                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(msg); }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "London Strategic Edge WS message parse error.");
                        continue;
                    }

                    using (doc)
                    {
                        var root = doc.RootElement;
                        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                            ? t.GetString()
                            : null;

                        switch (type)
                        {
                            case "tick":
                                var tick = ParseTick(root);
                                if (tick is null)
                                {
                                    if (Interlocked.Exchange(ref _loggedUnknownTick, 1) == 0)
                                        _logger.LogWarning("London Strategic Edge tick shape not recognised: {Json}", Truncate(root.GetRawText()));
                                    break;
                                }
                                var sym = root.TryGetProperty("symbol", out var sEl) && sEl.ValueKind == JsonValueKind.String
                                    ? sEl.GetString()
                                    : null;
                                if (sym is not null && _subs.TryGetValue(sym, out var sub))
                                    sub.Writer.TryWrite(tick);
                                break;

                            case "authenticated":
                                authenticatedThisSession = true;
                                everAuthenticated = true;
                                firstAuth.TrySetResult(true);
                                delay = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectInitialDelaySeconds));
                                _state.OnNext(Core.Domain.ConnectionState.Connected);
                                await ResubscribeAsync(ct).ConfigureAwait(false);
                                // Keepalive only matters once we're in — the server's idle timeout is 600s.
                                pingTask = RunPingLoopAsync(pingCts.Token);
                                break;

                            case "error":
                                var code = root.TryGetProperty("code", out var cEl) && cEl.ValueKind == JsonValueKind.String
                                    ? cEl.GetString() ?? string.Empty : string.Empty;
                                var message = root.TryGetProperty("message", out var mEl) && mEl.ValueKind == JsonValueKind.String
                                    ? mEl.GetString() : null;

                                if (FatalErrorCodes.Contains(code, StringComparer.OrdinalIgnoreCase) || !authenticatedThisSession)
                                {
                                    _logger.LogError(
                                        "London Strategic Edge fatal error {Code}: {Message} — stopping (no reconnect). " +
                                        "QUOTA_EXCEEDED means the 50 GB/month free tier is exhausted.",
                                        code, message);
                                    firstAuth.TrySetException(new InvalidOperationException($"LSE {code}: {message}"));
                                    fatal = true;
                                }
                                else
                                {
                                    _logger.LogWarning("London Strategic Edge error {Code}: {Message}", code, message);
                                }
                                break;

                            // welcome / subscribed / unsubscribed / pong / replay_* — protocol chatter.
                            default:
                                break;
                        }
                    }

                    if (fatal) break;
                }
            }
            finally
            {
                lock (_sync) { _ws = null; }
                pingCts.Cancel();
                if (pingTask is not null)
                {
                    try { await pingTask.ConfigureAwait(false); } catch { /* swallow */ }
                }
                pingCts.Dispose();
                try
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* swallow */ }
                ws.Dispose();
            }

            if (fatal)
            {
                _state.OnNext(Core.Domain.ConnectionState.Failed);
                return;
            }
            if (ct.IsCancellationRequested) return;
            if (!everAuthenticated)
            {
                // Socket closed before the server ever authenticated us — treat as a failed connect.
                firstAuth.TrySetException(new InvalidOperationException(
                    "London Strategic Edge closed the connection before authenticating — check the API key."));
                return;
            }

            _state.OnNext(Core.Domain.ConnectionState.Reconnecting);
            if (!await DelayAsync(delay, ct).ConfigureAwait(false)) return;
            delay = NextDelay(delay, maxDelay);
        }
    }

    private async Task ResubscribeAsync(CancellationToken ct)
    {
        foreach (var symbol in _subs.Keys)
        {
            if (ct.IsCancellationRequested) return;
            await TrySendAsync(new { action = "subscribe", symbol }, ct).ConfigureAwait(false);
        }
    }

    private async Task RunPingLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.PingIntervalSeconds));
        while (!ct.IsCancellationRequested)
        {
            if (!await DelayAsync(interval, ct).ConfigureAwait(false)) return;
            await TrySendAsync(new { action = "ping" }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Serializes and sends one JSON message on the live socket; false when down / send fails.</summary>
    private async Task<bool> TrySendAsync(object payload, CancellationToken ct)
    {
        ClientWebSocket? ws;
        lock (_sync) { ws = _ws; }
        if (ws is null || ws.State != WebSocketState.Open) return false;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        try
        {
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "London Strategic Edge WS send failed.");
            return false;
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private static TimeSpan NextDelay(TimeSpan delay, TimeSpan maxDelay) =>
        TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));

    /// <summary>Reads one full WebSocket text message (re-assembling continuation frames), or null on close/error/cancel.</summary>
    private static async Task<byte[]?> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
            catch (WebSocketException) { return null; }

            if (result.MessageType == WebSocketMessageType.Close) return null;

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return ms.ToArray();
    }

    // ── Parsers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tick message: <c>{type:"tick", symbol, price, bid, ask, volume, ts, name, replay}</c> with
    /// bid/ask nullable — when absent the tick degrades to last-price-as-both-sides (the ingest
    /// layer's Lee-Ready inference then sees a zero spread, which is the honest representation).
    /// Sizes are not in the feed, so both are 0. Pure and deterministic — null on no usable price.
    /// </summary>
    internal static Tick? ParseTick(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        var price = Number(el, "price");
        var bid = Number(el, "bid");
        var ask = Number(el, "ask");

        if (bid is null && ask is null)
        {
            if (price is null) return null;
            bid = price;
            ask = price;
        }

        return new Tick(Timestamp(el), bid ?? price ?? 0, ask ?? price ?? 0, 0, 0);
    }

    /// <summary>ISO-8601 <c>ts</c>/<c>timestamp</c> when present and parseable, else arrival time.</summary>
    internal static DateTime Timestamp(JsonElement el)
    {
        foreach (var key in new[] { "ts", "timestamp" })
        {
            if (!el.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) continue;
            if (DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                return dto.UtcDateTime;
        }
        return DateTime.UtcNow;
    }

    private static double? Number(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null,
            _ => null,
        };
    }

    // ── Symbol mapping ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a canonical <see cref="Contract"/> to an LSE symbol. LSE uses plain tickers for stocks /
    /// ETFs ("AAPL") and slash pairs for FX / crypto ("EUR/USD", "BTC/USD"). Symbols already carrying
    /// '/' pass through verbatim; a bare 6-letter CASH pair is split ("EURUSD" → "EUR/USD") so
    /// contracts coming from the shared instrument picker resolve without per-broker symbology.
    /// </summary>
    internal static string ToLseSymbol(Contract contract)
    {
        var symbol = contract.Symbol?.Trim() ?? string.Empty;
        if (symbol.Contains('/', StringComparison.Ordinal))
            return symbol;

        if (string.Equals(contract.SecType, "CASH", StringComparison.OrdinalIgnoreCase) &&
            symbol.Length == 6 && symbol.All(char.IsLetter))
        {
            return $"{symbol[..3].ToUpperInvariant()}/{symbol[3..].ToUpperInvariant()}";
        }

        return symbol;
    }

    private static string Truncate(string s) => s.Length <= 512 ? s : s[..512];

    // ── Internal subscription bookkeeping ───────────────────────────────────────────────────────

    /// <summary>An active tick subscription: its LSE symbol plus a bounded drop-oldest channel the pump fans into.</summary>
    private sealed class Subscription
    {
        private readonly Channel<Tick> _channel =
            FeedChannel.CreateDropOldest<Tick>(FeedChannel.Capacity.Quotes, singleWriter: false);

        public Subscription(string symbol) => Symbol = symbol;

        public string Symbol { get; }
        public ChannelWriter<Tick> Writer => _channel.Writer;
        public ChannelReader<Tick> Reader => _channel.Reader;

        public void Complete() => _channel.Writer.TryComplete();
    }
}
