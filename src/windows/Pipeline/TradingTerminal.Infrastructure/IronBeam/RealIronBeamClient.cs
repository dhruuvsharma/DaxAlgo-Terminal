using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
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

namespace TradingTerminal.Infrastructure.IronBeam;

/// <summary>
/// Ironbeam futures (FCM) market-data client over the hand-rolled REST + WebSocket API v2 — no SDK,
/// just <see cref="HttpClient"/> + <see cref="ClientWebSocket"/> (same shape as
/// <c>RealBinanceClient</c>). The connection model differs from Binance, though: Ironbeam multiplexes
/// <b>everything onto one server-created stream</b> rather than one socket per symbol.
/// <list type="number">
/// <item><c>POST /auth</c> (username + API key as <c>password</c>) → JWT bearer token.</item>
/// <item><c>GET /stream/create</c> → a <c>streamId</c>.</item>
/// <item>Open <c>wss://{host}/v2/stream/{streamId}?token=...</c> (token as a query param — WS can't
/// carry an <c>Authorization</c> header).</item>
/// <item>Subscribe with REST GETs after the WS is open:
/// <c>/market/quotes|depths|trades/subscribe/{streamId}?symbols=XCME:ES.U16,...</c> (≤10 symbols).</item>
/// </list>
/// One background read pump owns the single WS, demultiplexing each JSON message's event arrays
/// (<c>q</c> quotes, <c>d</c> depths, <c>tr</c> trades) and fanning them out to per-(symbol,kind)
/// channels. The <c>Subscribe*Async</c> methods register a channel, issue the REST subscribe, and
/// drain that channel as an <see cref="IAsyncEnumerable{T}"/>; cancellation unsubscribes (drops the
/// channel). On a socket drop the pump re-auths, re-creates the stream, and re-issues every active
/// subscription, with exponential backoff (<see cref="IronBeamOptions.ReconnectInitialDelaySeconds"/>
/// → <see cref="IronBeamOptions.ReconnectMaxDelaySeconds"/>).
///
/// WS field names beyond the documented ones can vary by deployment, so parsing is deliberately
/// defensive (<c>TryGetProperty</c> over plausible key variants; unknown shapes are logged once, not
/// thrown). This is data-only — there is no order path (the whole build is data/signals only).
/// </summary>
internal sealed class RealIronBeamClient : IBrokerClient
{
    private readonly ILogger<RealIronBeamClient> _logger;
    private readonly IronBeamOptions _options;
    private readonly HttpClient _http = new();
    private readonly BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);

    // The active stream's plumbing. Recreated on (re)connect; guarded by _sync.
    private readonly object _sync = new();
    private string? _token;
    private string? _streamId;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

    // Active subscriptions, keyed by channel+symbol so a reconnect can re-issue them and the pump
    // can fan out incoming events to the matching consumer channels.
    private readonly ConcurrentDictionary<SubKey, Subscription> _subs = new();

    // One-shot guards so an unexpected WS shape is logged once, not on every message.
    private int _loggedUnknownQuote;
    private int _loggedUnknownDepth;
    private int _loggedUnknownTrade;

    public RealIronBeamClient(ILogger<RealIronBeamClient> logger, IOptions<IronBeamOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public BrokerKind Kind => BrokerKind.IronBeam;

    public IObservable<ConnectionState> ConnectionState => _state.AsObservable();

    // ── Connect / disconnect ────────────────────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Connecting);
        try
        {
            await AuthenticateAndOpenStreamAsync(ct).ConfigureAwait(false);

            // Long-lived pump owns the WS for the session; its cancellation is independent of the
            // connect call's token so the stream survives after ConnectAsync returns.
            var pumpCts = new CancellationTokenSource();
            var pumpTask = RunPumpAsync(pumpCts.Token);
            lock (_sync)
            {
                _pumpCts = pumpCts;
                _pumpTask = pumpTask;
            }

            _logger.LogInformation(
                "Ironbeam connected — stream {StreamId} at {Host} ({Mode}).",
                _streamId, BaseUrl, _options.IsLive ? "live" : "demo");
            _state.OnNext(Core.Domain.ConnectionState.Connected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _state.OnNext(Core.Domain.ConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ironbeam connect failed reaching {Host}.", BaseUrl);
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

        // Complete every consumer channel so in-flight Subscribe*Async loops finish gracefully.
        foreach (var sub in _subs.Values)
            sub.Complete();

        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync().ConfigureAwait(false); } catch { /* swallow */ }
        _http.Dispose();
        _state.Dispose();
    }

    // ── Instruments ─────────────────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
    {
        // Ironbeam's symbol universe is broker-specific (EXCHANGE:SYMBOL.MonthCodeYY) and not
        // cleanly enumerable from a documented v2 endpoint; the picker falls back to the curated
        // SignalInstrumentCatalog when this is empty, so we return nothing here (cold catalog).
        return Task.FromResult<IReadOnlyList<TradableInstrument>>(Array.Empty<TradableInstrument>());
    }

    // ── Historical bars ─────────────────────────────────────────────────────────────────────────

    private int _loggedNoHistory;

    public Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        // The v2 API exposes only *streaming* bar indicators — there is no documented REST history
        // endpoint. Return empty (strategies treat empty history as a cold start) and warn once.
        if (Interlocked.Exchange(ref _loggedNoHistory, 1) == 0)
        {
            _logger.LogWarning(
                "Ironbeam has no documented REST historical-bars endpoint; returning empty history (cold start). Live bars stream via SubscribeBarsAsync.");
        }
        return Task.FromResult<IReadOnlyList<Bar>>(Array.Empty<Bar>());
    }

    // ── Streaming subscriptions ─────────────────────────────────────────────────────────────────

    public IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize, CancellationToken ct = default)
    {
        // No documented REST history; and no dedicated WS bar-subscribe call is mapped here. Bars are
        // aggregated downstream from the tick-primary feed (L1 quotes + trade tape), so the bar stream
        // stays empty rather than guessing at an undocumented endpoint.
        return EmptyAsync<Bar>(ct);
    }

    public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
        StreamAsync<Tick>(StreamKind.Quote, contract, ct);

    public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, CancellationToken ct = default) =>
        StreamAsync<DepthSnapshot>(StreamKind.Depth, contract, ct);

    public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        StreamAsync<TradeTick>(StreamKind.Trade, contract, ct);

    /// <summary>
    /// Registers a per-(symbol,kind) channel, issues the REST subscribe against the live stream, and
    /// drains the channel. Cancellation removes the subscription (the unsubscribe path) and completes
    /// the channel. The pump fans matching WS events into the channel; on reconnect the pump re-issues
    /// the REST subscribe for every still-registered subscription.
    /// </summary>
    private async IAsyncEnumerable<T> StreamAsync<T>(
        StreamKind kind, Contract contract, [EnumeratorCancellation] CancellationToken ct) where T : class
    {
        var symbol = ToIronBeamSymbol(contract);
        var key = new SubKey(kind, symbol);
        var sub = new Subscription(kind, symbol);

        // Last writer wins; if a duplicate subscription exists, complete the old one first.
        if (_subs.TryGetValue(key, out var existing) && !ReferenceEquals(existing, sub))
            existing.Complete();
        _subs[key] = sub;

        try
        {
            // Issue the REST subscribe against the current stream (best-effort — if the WS is down the
            // pump re-issues on reconnect).
            await TrySubscribeRestAsync(kind, symbol, ct).ConfigureAwait(false);

            await foreach (var item in sub.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (item is T typed)
                    yield return typed;
            }
        }
        finally
        {
            _subs.TryRemove(new KeyValuePair<SubKey, Subscription>(key, sub));
            sub.Complete();
        }
    }

    private static async IAsyncEnumerable<T> EmptyAsync<T>([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
#pragma warning disable CS0162 // unreachable — shape required to make this an async iterator
        yield return default!;
#pragma warning restore CS0162
    }

    // ── Auth + stream creation ──────────────────────────────────────────────────────────────────

    private string BaseUrl => string.IsNullOrWhiteSpace(_options.BaseUrlOverride)
        ? (_options.IsLive ? "https://live.ironbeamapi.com/v2" : "https://demo.ironbeamapi.com/v2")
        : _options.BaseUrlOverride.TrimEnd('/');

    private async Task AuthenticateAndOpenStreamAsync(CancellationToken ct)
    {
        _token = await AuthenticateAsync(ct).ConfigureAwait(false);
        _streamId = await CreateStreamAsync(_token, ct).ConfigureAwait(false);
    }

    /// <summary><c>POST /auth</c> {username, password=ApiKey} → JWT in <c>token</c>.</summary>
    private async Task<string> AuthenticateAsync(CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { username = _options.Username, password = _options.ApiKey });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/auth")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bytes);

        if (!doc.RootElement.TryGetProperty("token", out var tokenEl) || tokenEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Ironbeam /auth returned no token.");

        var token = tokenEl.GetString();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Ironbeam /auth returned an empty token.");
        return token;
    }

    /// <summary><c>GET /stream/create</c> → <c>streamId</c>.</summary>
    private async Task<string> CreateStreamAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/stream/create");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bytes);

        if (!doc.RootElement.TryGetProperty("streamId", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Ironbeam /stream/create returned no streamId.");

        var streamId = idEl.GetString();
        if (string.IsNullOrWhiteSpace(streamId))
            throw new InvalidOperationException("Ironbeam /stream/create returned an empty streamId.");
        return streamId;
    }

    /// <summary>Builds <c>wss://{host}/v2/stream/{streamId}?token=...</c> from the REST base URL.</summary>
    private Uri BuildWebSocketUri(string streamId, string token)
    {
        var rest = new Uri(BaseUrl);
        var scheme = rest.Scheme == "https" ? "wss" : "ws";
        var path = rest.AbsolutePath.TrimEnd('/'); // e.g. "/v2"
        return new Uri($"{scheme}://{rest.Host}{path}/stream/{streamId}?token={Uri.EscapeDataString(token)}");
    }

    private async Task<bool> TrySubscribeRestAsync(StreamKind kind, string symbol, CancellationToken ct)
    {
        string? token, streamId;
        lock (_sync) { token = _token; streamId = _streamId; }
        if (token is null || streamId is null) return false;

        var path = kind switch
        {
            StreamKind.Quote => "market/quotes/subscribe",
            StreamKind.Depth => "market/depths/subscribe",
            StreamKind.Trade => "market/trades/subscribe",
            _ => null,
        };
        if (path is null) return false;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{BaseUrl}/{path}/{streamId}?symbols={Uri.EscapeDataString(symbol)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ironbeam {Kind} subscribe failed for {Symbol}.", kind, symbol);
            return false;
        }
    }

    // ── The single read pump (owns the WS, demuxes events, drives reconnect) ────────────────────

    private async Task RunPumpAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectInitialDelaySeconds));
        var maxDelay = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectMaxDelaySeconds));
        var firstAttempt = true;

        while (!ct.IsCancellationRequested)
        {
            string? token, streamId;
            lock (_sync) { token = _token; streamId = _streamId; }

            // On reconnect (not the first loop) re-auth + create a fresh stream — the old stream id
            // is dead once the socket dropped.
            if (!firstAttempt || token is null || streamId is null)
            {
                _state.OnNext(Core.Domain.ConnectionState.Reconnecting);
                try
                {
                    await AuthenticateAndOpenStreamAsync(ct).ConfigureAwait(false);
                    lock (_sync) { token = _token; streamId = _streamId; }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Ironbeam re-auth/stream-create failed; retrying in {Delay}s.", delay.TotalSeconds);
                    if (!await DelayAsync(delay, ct).ConfigureAwait(false)) return;
                    delay = NextDelay(delay, maxDelay);
                    continue;
                }
            }
            firstAttempt = false;

            ClientWebSocket? ws = new();
            try
            {
                await ws.ConnectAsync(BuildWebSocketUri(streamId!, token!), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ws.Dispose();
                return;
            }
            catch (Exception ex)
            {
                ws.Dispose();
                ws = null;
                _logger.LogWarning(ex, "Ironbeam WS connect failed; retrying in {Delay}s.", delay.TotalSeconds);
            }

            if (ws is not null)
            {
                delay = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectInitialDelaySeconds)); // reset backoff after a clean connect
                _state.OnNext(Core.Domain.ConnectionState.Connected);

                // Re-issue every still-registered subscription against the (new) stream.
                await ReissueSubscriptionsAsync(ct).ConfigureAwait(false);

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var msg = await ReceiveMessageAsync(ws, ct).ConfigureAwait(false);
                        if (msg is null) break; // close / error → fall out and reconnect

                        try
                        {
                            using var doc = JsonDocument.Parse(msg);
                            Dispatch(doc.RootElement);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Ironbeam WS message parse error.");
                        }
                    }
                }
                finally
                {
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { /* swallow */ }
                    ws.Dispose();
                }
            }

            if (ct.IsCancellationRequested) return;

            // The socket dropped — invalidate the stream so the next loop re-auths/re-creates.
            lock (_sync) { _token = null; _streamId = null; }
            _state.OnNext(Core.Domain.ConnectionState.Reconnecting);
            if (!await DelayAsync(delay, ct).ConfigureAwait(false)) return;
            delay = NextDelay(delay, maxDelay);
        }
    }

    private async Task ReissueSubscriptionsAsync(CancellationToken ct)
    {
        foreach (var sub in _subs.Values)
        {
            if (ct.IsCancellationRequested) return;
            await TrySubscribeRestAsync(sub.Kind, sub.Symbol, ct).ConfigureAwait(false);
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

    // ── Demux: route each event array to its per-(symbol,kind) channel ──────────────────────────

    private void Dispatch(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("q", out var quotes) && quotes.ValueKind == JsonValueKind.Array)
            foreach (var q in quotes.EnumerateArray())
            {
                var tick = ParseQuote(q);
                if (tick is null && Interlocked.Exchange(ref _loggedUnknownQuote, 1) == 0)
                    _logger.LogWarning("Ironbeam quote shape not recognised: {Json}", Truncate(q.GetRawText()));
                Fan(StreamKind.Quote, SymbolOf(q), tick);
            }

        if (root.TryGetProperty("d", out var depths) && depths.ValueKind == JsonValueKind.Array)
            foreach (var d in depths.EnumerateArray())
            {
                var depth = ParseDepth(d);
                if (depth is null && Interlocked.Exchange(ref _loggedUnknownDepth, 1) == 0)
                    _logger.LogWarning("Ironbeam depth shape not recognised: {Json}", Truncate(d.GetRawText()));
                Fan(StreamKind.Depth, SymbolOf(d), depth);
            }

        if (root.TryGetProperty("tr", out var trades) && trades.ValueKind == JsonValueKind.Array)
            foreach (var t in trades.EnumerateArray())
            {
                var trade = ParseTrade(t);
                if (trade is null && Interlocked.Exchange(ref _loggedUnknownTrade, 1) == 0)
                    _logger.LogWarning("Ironbeam trade shape not recognised: {Json}", Truncate(t.GetRawText()));
                Fan(StreamKind.Trade, SymbolOf(t), trade);
            }
    }

    private void Fan(StreamKind kind, string? symbol, object? item)
    {
        if (item is null || string.IsNullOrEmpty(symbol)) return;
        if (_subs.TryGetValue(new SubKey(kind, symbol), out var sub))
            sub.Writer.TryWrite(item);
    }

    // ── Parsers (defensive — Ironbeam field names beyond the documented ones can vary) ──────────

    private static string? SymbolOf(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "s", "symbol", "sym" })
            if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    /// <summary>
    /// Quote event (<c>q</c>): last + bid/ask price/size. Symbol-keyed by the caller. Pure and
    /// deterministic — returns <c>null</c> on an unrecognised shape (the caller logs once).
    /// </summary>
    internal static Tick? ParseQuote(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        var bid = FirstNumber(el, "b", "bp", "bid", "bidPrice");
        var ask = FirstNumber(el, "a", "ap", "ask", "askPrice");
        var bidSize = FirstNumber(el, "bs", "bsz", "bidSize");
        var askSize = FirstNumber(el, "as", "asz", "askSize");

        if (bid is null && ask is null)
        {
            // Some quote shapes carry only "last" (l) — fall back so the consumer still sees price.
            var last = FirstNumber(el, "l", "last", "lp");
            if (last is null)
                return null;
            bid ??= last;
            ask ??= last;
        }

        return new Tick(
            EventTime(el),
            bid ?? 0,
            ask ?? 0,
            ToSize(bidSize),
            ToSize(askSize));
    }

    /// <summary>
    /// Depth event (<c>d</c>): bid/ask level arrays. Each level is [price, size] or {p,sz}. Pure and
    /// deterministic — returns <c>null</c> on an unrecognised shape (the caller logs once).
    /// </summary>
    internal static DepthSnapshot? ParseDepth(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        var bids = ReadLevels(FirstArray(el, "b", "bids", "bid"));
        var asks = ReadLevels(FirstArray(el, "a", "asks", "ask"));

        if (bids.Count == 0 && asks.Count == 0)
            return null;

        return new DepthSnapshot(EventTime(el), bids, asks);
    }

    /// <summary>
    /// Trade event (<c>tr</c>): price (<c>p</c>), size (<c>sz</c>), timestamp (<c>st</c>), direction
    /// (<c>td</c>). Pure and deterministic — returns <c>null</c> when no price is present (caller logs once).
    /// </summary>
    internal static TradeTick? ParseTrade(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        var price = FirstNumber(el, "p", "price", "tp", "last");
        if (price is null)
            return null;

        var size = FirstNumber(el, "sz", "s", "size", "qty", "volume") ?? 0;
        var aggressor = ParseDirection(el);
        return new TradeTick(EventTime(el), price.Value, (long)Math.Round(size), aggressor);
    }

    /// <summary>Trade direction (<c>td</c>): "B"/"buy"/1 → Buy, "S"/"sell"/-1 → Sell, else Unknown.</summary>
    private static AggressorSide ParseDirection(JsonElement el)
    {
        foreach (var key in new[] { "td", "dir", "direction", "side", "aggressor" })
        {
            if (!el.TryGetProperty(key, out var v)) continue;
            switch (v.ValueKind)
            {
                case JsonValueKind.String:
                    var s = v.GetString();
                    if (string.IsNullOrEmpty(s)) continue;
                    var c = char.ToUpperInvariant(s[0]);
                    if (c == 'B') return AggressorSide.Buy;
                    if (c == 'S') return AggressorSide.Sell;
                    break;
                case JsonValueKind.Number when v.TryGetInt64(out var n):
                    if (n > 0) return AggressorSide.Buy;
                    if (n < 0) return AggressorSide.Sell;
                    break;
            }
        }
        return AggressorSide.Unknown;
    }

    /// <summary>Event timestamp from the message when present (epoch ms, epoch s, or ISO-8601), else arrival time.</summary>
    private static DateTime EventTime(JsonElement el)
    {
        foreach (var key in new[] { "st", "t", "ts", "time", "timestamp" })
        {
            if (!el.TryGetProperty(key, out var v)) continue;
            switch (v.ValueKind)
            {
                case JsonValueKind.Number when v.TryGetInt64(out var n):
                    // Heuristic: > ~year 2001 in seconds means it's already milliseconds.
                    return n > 100_000_000_000L
                        ? DateTimeOffset.FromUnixTimeMilliseconds(n).UtcDateTime
                        : DateTimeOffset.FromUnixTimeSeconds(n).UtcDateTime;
                case JsonValueKind.String:
                    if (DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                        return dto.UtcDateTime;
                    break;
            }
        }
        return DateTime.UtcNow;
    }

    private static IReadOnlyList<DepthLevel> ReadLevels(JsonElement side)
    {
        var levels = new List<DepthLevel>();
        if (side.ValueKind != JsonValueKind.Array) return levels;

        foreach (var lvl in side.EnumerateArray())
        {
            double? price, size;
            if (lvl.ValueKind == JsonValueKind.Array && lvl.GetArrayLength() >= 2)
            {
                price = AsNumber(lvl[0]);
                size = AsNumber(lvl[1]);
            }
            else if (lvl.ValueKind == JsonValueKind.Object)
            {
                price = FirstNumber(lvl, "p", "price", "px");
                size = FirstNumber(lvl, "sz", "s", "size", "qty");
            }
            else continue;

            if (price is null) continue;
            levels.Add(new DepthLevel(price.Value, (long)Math.Round(size ?? 0)));
        }
        return levels;
    }

    // ── Symbol mapping ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a canonical <see cref="Contract"/> to an Ironbeam symbol (EXCHANGE:SYMBOL.MonthCodeYY).
    /// If <see cref="Contract.Symbol"/> already contains ':' it's passed through verbatim (the picker
    /// can carry the full Ironbeam symbol); otherwise we compose <c>{Exchange}:{Symbol}</c> with a
    /// default exchange of <c>XCME</c>. NOTE: the canonical <c>Contract</c> record carries no expiry /
    /// month-code field, so an expiry suffix cannot be appended here — callers that need a specific
    /// contract month should supply the full <c>EXCHANGE:SYMBOL.MonthCodeYY</c> in <c>Symbol</c>.
    /// </summary>
    internal static string ToIronBeamSymbol(Contract contract)
    {
        var symbol = contract.Symbol?.Trim() ?? string.Empty;
        if (symbol.Contains(':', StringComparison.Ordinal))
            return symbol;

        var exchange = string.IsNullOrWhiteSpace(contract.Exchange) ? "XCME" : contract.Exchange.Trim();
        return $"{exchange}:{symbol}";
    }

    // ── Value helpers ───────────────────────────────────────────────────────────────────────────

    private static double? FirstNumber(JsonElement el, params string[] keys)
    {
        foreach (var key in keys)
            if (el.TryGetProperty(key, out var v))
            {
                var n = AsNumber(v);
                if (n is not null) return n;
            }
        return null;
    }

    private static JsonElement FirstArray(JsonElement el, params string[] keys)
    {
        foreach (var key in keys)
            if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array)
                return v;
        return default;
    }

    private static double? AsNumber(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null,
            _ => null,
        };

    private static long ToSize(double? qty) => qty is null ? 0 : (long)Math.Round(qty.Value);

    private static string Truncate(string s) => s.Length <= 512 ? s : s[..512];

    // ── Internal subscription bookkeeping ───────────────────────────────────────────────────────

    private enum StreamKind { Quote, Depth, Trade }

    private readonly record struct SubKey(StreamKind Kind, string Symbol);

    /// <summary>An active subscription: its identity plus a bounded drop-oldest channel the pump fans into.</summary>
    private sealed class Subscription
    {
        private readonly Channel<object> _channel;

        public Subscription(StreamKind kind, string symbol)
        {
            Kind = kind;
            Symbol = symbol;
            _channel = FeedChannel.CreateDropOldest<object>(
                kind switch
                {
                    StreamKind.Trade => FeedChannel.Capacity.Trades,
                    StreamKind.Depth => FeedChannel.Capacity.Depth,
                    _ => FeedChannel.Capacity.Quotes,
                },
                singleWriter: false);
        }

        public StreamKind Kind { get; }
        public string Symbol { get; }
        public ChannelWriter<object> Writer => _channel.Writer;
        public ChannelReader<object> Reader => _channel.Reader;

        public void Complete() => _channel.Writer.TryComplete();
    }
}
