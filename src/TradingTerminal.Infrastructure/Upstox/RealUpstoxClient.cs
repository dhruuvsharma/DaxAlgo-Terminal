using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
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

namespace TradingTerminal.Infrastructure.Upstox;

/// <summary>
/// Upstox (Indian-market) data client over the hand-rolled REST + WebSocket API v2/v3 — no SDK, just
/// <see cref="HttpClient"/> + <see cref="ClientWebSocket"/> (same shape as <c>RealIronBeamClient</c>).
/// OAuth2 access token is supplied via <see cref="UpstoxOptions.AccessToken"/> (the login form runs the
/// authorization-code exchange). Live data:
/// <list type="number">
/// <item><c>GET /v3/feed/market-data-feed/authorize</c> (Bearer token) → an authorized <c>wss://</c> URL.</item>
/// <item>Open that socket; send a <b>binary</b> JSON subscribe message
/// (<c>{method:"sub",data:{mode:"full",instrumentKeys:[…]}}</c>).</item>
/// <item>Incoming frames are binary protobuf <c>FeedResponse</c>s, decoded by
/// <see cref="UpstoxFeedDecoder"/> into per-instrument LTP + 5-level book.</item>
/// </list>
/// One background pump owns the single WS, decodes each frame, and fans the resulting L1 quote /
/// depth snapshot to per-(instrumentKey,kind) channels. On a socket drop the pump re-authorizes,
/// reconnects, and re-subscribes every active key with exponential backoff
/// (<see cref="UpstoxOptions.ReconnectInitialDelaySeconds"/> → <see cref="UpstoxOptions.ReconnectMaxDelaySeconds"/>).
///
/// <para>Historical candles and the instrument master come over REST. There is <b>no real trade
/// tape</b> (the feed carries LTP + book, not per-print flow) — <see cref="SubscribeTradesAsync"/>
/// throws and ingest falls back to the synthetic L1 tick rule. Data-only (the whole build is
/// data/signals only).</para>
/// </summary>
internal sealed class RealUpstoxClient : IBrokerClient
{
    private readonly ILogger<RealUpstoxClient> _logger;
    private readonly UpstoxOptions _options;
    private readonly HttpClient _http = new();
    private readonly BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);

    private readonly object _sync = new();
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

    // Active subscriptions, keyed by (kind, instrumentKey) so a reconnect can re-issue them and the
    // pump can fan decoded feeds to the matching consumer channels.
    private readonly ConcurrentDictionary<SubKey, Subscription> _subs = new();

    // Cached instrument master (downloaded once on first ListInstrumentsAsync).
    private IReadOnlyList<TradableInstrument>? _instrumentCache;
    private int _loggedNoToken;

    public RealUpstoxClient(ILogger<RealUpstoxClient> logger, IOptions<UpstoxOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public BrokerKind Kind => BrokerKind.Upstox;

    public IObservable<ConnectionState> ConnectionState => _state.AsObservable();

    private string BaseUrl => string.IsNullOrWhiteSpace(_options.BaseUrl)
        ? "https://api.upstox.com"
        : _options.BaseUrl.TrimEnd('/');

    // ── Connect / disconnect ────────────────────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Connecting);
        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            _logger.LogError("Upstox connect failed — no access token. Authorize in the login form first.");
            _state.OnNext(Core.Domain.ConnectionState.Failed);
            return;
        }

        try
        {
            // Probe the token by authorizing the feed; this also validates connectivity before we
            // report Connected.
            _ = await AuthorizeFeedAsync(ct).ConfigureAwait(false);

            var pumpCts = new CancellationTokenSource();
            var pumpTask = RunPumpAsync(pumpCts.Token);
            lock (_sync)
            {
                _pumpCts = pumpCts;
                _pumpTask = pumpTask;
            }

            _logger.LogInformation("Upstox connected — V3 market-data feed at {Host}.", BaseUrl);
            _state.OnNext(Core.Domain.ConnectionState.Connected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _state.OnNext(Core.Domain.ConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upstox connect failed reaching {Host}.", BaseUrl);
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

        foreach (var sub in _subs.Values) sub.Complete();
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync().ConfigureAwait(false); } catch { /* swallow */ }
        _http.Dispose();
        _sendLock.Dispose();
        _state.Dispose();
    }

    // ── Instruments (downloadable master, cached) ────────────────────────────────────────────────

    public async Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
    {
        if (_instrumentCache is not null) return _instrumentCache;
        try
        {
            // NSE master (equities + indices + F&O). We keep only cash equities + indices so the
            // picker stays manageable; the gz is fetched once and cached for the session.
            const string url = "https://assets.upstox.com/market-quote/instruments/exchange/NSE.json.gz";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var gz = new GZipStream(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), CompressionMode.Decompress);
            using var doc = await JsonDocument.ParseAsync(gz, cancellationToken: ct).ConfigureAwait(false);

            var list = new List<TradableInstrument>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var inst = ParseMasterRow(el);
                    if (inst is not null) list.Add(inst);
                }
            }
            _instrumentCache = list;
            _logger.LogInformation("Upstox instrument master loaded — {Count} NSE equities/indices.", list.Count);
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Upstox instrument master load failed; picker falls back to the curated catalog.");
            return Array.Empty<TradableInstrument>();
        }
    }

    private static TradableInstrument? ParseMasterRow(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var segment = Str(el, "segment");
        // Keep cash equities + indices only.
        if (segment is not ("NSE_EQ" or "NSE_INDEX")) return null;

        var key = Str(el, "instrument_key");
        if (string.IsNullOrEmpty(key)) return null;
        var symbol = Str(el, "trading_symbol") ?? Str(el, "name") ?? key;
        var isIndex = segment == "NSE_INDEX";

        var contract = new Contract(
            Symbol: key,                 // carry the canonical Upstox instrument_key through Symbol
            SecType: isIndex ? "IND" : "EQ",
            Exchange: "NSE",
            Currency: "INR",
            PrimaryExchange: "NSE");
        return new TradableInstrument(symbol!, isIndex ? "NSE Index" : "NSE Equity", contract, BrokerKind.Upstox);
    }

    // ── Historical bars (REST) ────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        var key = ToInstrumentKey(contract);
        var interval = ToHistoricalInterval(barSize);
        // Upstox dates are in IST; use the exchange day boundaries.
        var ist = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ist);
        var toDate = nowIst.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fromDate = nowIst.Subtract(duration <= TimeSpan.Zero ? TimeSpan.FromDays(5) : duration)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var url = $"{BaseUrl}/v2/historical-candle/{Uri.EscapeDataString(key)}/{interval}/{toDate}/{fromDate}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return ParseCandles(bytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Upstox historical-candle fetch failed for {Key} ({Interval}).", key, interval);
            return Array.Empty<Bar>();
        }
    }

    /// <summary>Parses <c>{ data: { candles: [[ts, o, h, l, c, vol, oi], …] } }</c> (newest-first) into
    /// ascending-time <see cref="Bar"/>s.</summary>
    internal static IReadOnlyList<Bar> ParseCandles(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("candles", out var candles) ||
            candles.ValueKind != JsonValueKind.Array)
            return Array.Empty<Bar>();

        var bars = new List<Bar>(candles.GetArrayLength());
        foreach (var c in candles.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Array || c.GetArrayLength() < 6) continue;
            if (!DateTimeOffset.TryParse(c[0].GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts))
                continue;
            bars.Add(new Bar(
                ts.UtcDateTime,
                c[1].GetDouble(), c[2].GetDouble(), c[3].GetDouble(), c[4].GetDouble(),
                (long)Math.Round(c[5].GetDouble())));
        }
        bars.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc)); // Upstox returns newest-first
        return bars;
    }

    // ── Streaming subscriptions ───────────────────────────────────────────────────────────────────

    public IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, CancellationToken ct = default)
        // Bars are aggregated downstream from the tick-primary feed; no dedicated WS bar channel.
        => EmptyAsync<Bar>(ct);

    public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
        StreamAsync<Tick>(StreamKind.Quote, contract, ct);

    public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default) =>
        StreamAsync<DepthSnapshot>(StreamKind.Depth, contract, ct);

    public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Upstox's market-data feed carries LTP + book, not a per-print trade tape. " +
            "Trade-driven strategies fall back to the synthetic L1 tick rule.");

    /// <summary>
    /// Registers a per-(kind,instrumentKey) channel, sends the WS subscribe for the key, and drains the
    /// channel. Cancellation unsubscribes (sends <c>unsub</c>, drops the channel). The pump fans decoded
    /// feeds into the channel; on reconnect the pump re-subscribes every still-registered key.
    /// </summary>
    private async IAsyncEnumerable<T> StreamAsync<T>(
        StreamKind kind, Contract contract, [EnumeratorCancellation] CancellationToken ct) where T : class
    {
        var key = ToInstrumentKey(contract);
        var subKey = new SubKey(kind, key);
        var sub = new Subscription(kind, key);

        if (_subs.TryGetValue(subKey, out var existing) && !ReferenceEquals(existing, sub))
            existing.Complete();
        _subs[subKey] = sub;

        try
        {
            await TrySendSubAsync("sub", key, ct).ConfigureAwait(false);
            await foreach (var item in sub.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                if (item is T typed)
                    yield return typed;
        }
        finally
        {
            _subs.TryRemove(new KeyValuePair<SubKey, Subscription>(subKey, sub));
            sub.Complete();
            // Unsubscribe only when no other kind still wants this key.
            if (!_subs.Keys.Any(k => k.InstrumentKey == key))
                await TrySendSubAsync("unsub", key, CancellationToken.None).ConfigureAwait(false);
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

    // ── Feed authorize + the single read pump ─────────────────────────────────────────────────────

    /// <summary><c>GET /v3/feed/market-data-feed/authorize</c> → authorized <c>wss://</c> URL.</summary>
    private async Task<Uri> AuthorizeFeedAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v3/feed/market-data-feed/authorize");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bytes);

        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var key in new[] { "authorized_redirect_uri", "authorizedRedirectUri", "authorized_redirect_url" })
                if (data.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    && Uri.TryCreate(v.GetString(), UriKind.Absolute, out var uri))
                    return uri;
        }
        throw new InvalidOperationException("Upstox feed authorize returned no authorized WebSocket URL.");
    }

    private async Task RunPumpAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectInitialDelaySeconds));
        var maxDelay = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectMaxDelaySeconds));

        while (!ct.IsCancellationRequested)
        {
            Uri wssUri;
            try
            {
                wssUri = await AuthorizeFeedAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Upstox feed authorize failed; retrying in {Delay}s.", delay.TotalSeconds);
                _state.OnNext(Core.Domain.ConnectionState.Reconnecting);
                if (!await DelayAsync(delay, ct).ConfigureAwait(false)) return;
                delay = NextDelay(delay, maxDelay);
                continue;
            }

            var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(wssUri, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { ws.Dispose(); return; }
            catch (Exception ex)
            {
                ws.Dispose();
                _logger.LogWarning(ex, "Upstox WS connect failed; retrying in {Delay}s.", delay.TotalSeconds);
                _state.OnNext(Core.Domain.ConnectionState.Reconnecting);
                if (!await DelayAsync(delay, ct).ConfigureAwait(false)) return;
                delay = NextDelay(delay, maxDelay);
                continue;
            }

            lock (_sync) { _ws = ws; }
            delay = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectInitialDelaySeconds)); // reset backoff
            _state.OnNext(Core.Domain.ConnectionState.Connected);

            // (Re)subscribe every still-registered instrument key against the fresh socket.
            await ResubscribeAllAsync(ws, ct).ConfigureAwait(false);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var msg = await ReceiveMessageAsync(ws, ct).ConfigureAwait(false);
                    if (msg is null) break; // close / error → reconnect
                    try
                    {
                        foreach (var feed in UpstoxFeedDecoder.Decode(msg))
                            FanFeed(feed);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Upstox feed decode error.");
                    }
                }
            }
            finally
            {
                lock (_sync) { if (ReferenceEquals(_ws, ws)) _ws = null; }
                try
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* swallow */ }
                ws.Dispose();
            }

            if (ct.IsCancellationRequested) return;
            _state.OnNext(Core.Domain.ConnectionState.Reconnecting);
            if (!await DelayAsync(delay, ct).ConfigureAwait(false)) return;
            delay = NextDelay(delay, maxDelay);
        }
    }

    private async Task ResubscribeAllAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var keys = _subs.Keys.Select(k => k.InstrumentKey).Distinct().ToArray();
        if (keys.Length == 0) return;
        await SendSubMessageAsync(ws, "sub", keys, ct).ConfigureAwait(false);
    }

    private async Task<bool> TrySendSubAsync(string method, string instrumentKey, CancellationToken ct)
    {
        ClientWebSocket? ws;
        lock (_sync) { ws = _ws; }
        if (ws is null || ws.State != WebSocketState.Open) return false; // pump re-subscribes on (re)connect
        try
        {
            await SendSubMessageAsync(ws, method, new[] { instrumentKey }, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Upstox {Method} for {Key} failed.", method, instrumentKey);
            return false;
        }
    }

    /// <summary>Sends a <c>sub</c>/<c>unsub</c> control message. Upstox expects it as a <b>binary</b>
    /// frame whose payload is the UTF-8 JSON. Serialized through <see cref="_sendLock"/> since
    /// <see cref="ClientWebSocket"/> forbids concurrent sends.</summary>
    private async Task SendSubMessageAsync(ClientWebSocket ws, string method, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            guid = Guid.NewGuid().ToString("N"),
            method,
            data = new { mode = "full", instrumentKeys = keys },
        });
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    // ── Fan decoded feeds to consumer channels ─────────────────────────────────────────────────────

    private void FanFeed(UpstoxFeed feed)
    {
        if (string.IsNullOrEmpty(feed.InstrumentKey)) return;
        var when = DateTime.UtcNow;

        // L1 quote: top of book, or LTP on both sides when only LTPC is present (indices / ltpc mode).
        if (_subs.TryGetValue(new SubKey(StreamKind.Quote, feed.InstrumentKey), out var qSub))
        {
            var top = feed.Levels.Count > 0 ? feed.Levels[0] : default;
            var bid = feed.Levels.Count > 0 && top.BidPrice > 0 ? top.BidPrice : feed.Ltp ?? 0;
            var ask = feed.Levels.Count > 0 && top.AskPrice > 0 ? top.AskPrice : feed.Ltp ?? 0;
            if (bid > 0 || ask > 0)
                qSub.Writer.TryWrite(new Tick(when, bid, ask, top.BidQty, top.AskQty));
        }

        // L2 depth: the full bid/ask ladder.
        if (feed.Levels.Count > 0 && _subs.TryGetValue(new SubKey(StreamKind.Depth, feed.InstrumentKey), out var dSub))
        {
            var bids = new List<DepthLevel>(feed.Levels.Count);
            var asks = new List<DepthLevel>(feed.Levels.Count);
            foreach (var lvl in feed.Levels)
            {
                if (lvl.BidPrice > 0) bids.Add(new DepthLevel(lvl.BidPrice, lvl.BidQty));
                if (lvl.AskPrice > 0) asks.Add(new DepthLevel(lvl.AskPrice, lvl.AskQty));
            }
            dSub.Writer.TryWrite(new DepthSnapshot(when, bids, asks));
        }
    }

    // ── WS receive (re-assemble continuation frames) ───────────────────────────────────────────────

    private static async Task<byte[]?> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try { result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
            catch (WebSocketException) { return null; }

            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return ms.ToArray();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Maps a canonical <see cref="Contract"/> to an Upstox instrument key. Picker rows carry
    /// the full key (e.g. <c>NSE_EQ|INE002A01018</c>) in <see cref="Contract.Symbol"/>, so a value
    /// containing '|' is passed through; otherwise we best-effort compose <c>NSE_EQ|{Symbol}</c>.</summary>
    internal static string ToInstrumentKey(Contract contract)
    {
        var symbol = contract.Symbol?.Trim() ?? string.Empty;
        if (symbol.Contains('|', StringComparison.Ordinal)) return symbol;
        return $"NSE_EQ|{symbol}";
    }

    /// <summary>Maps a <see cref="BarSize"/> to the closest Upstox V2 historical interval (the API
    /// supports 1minute / 30minute / day / week / month only).</summary>
    private static string ToHistoricalInterval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1minute",
        BarSize.ThreeMinutes or BarSize.FiveMinutes or BarSize.FifteenMinutes or BarSize.OneHour => "30minute",
        _ => "day",
    };

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private static TimeSpan NextDelay(TimeSpan delay, TimeSpan maxDelay) =>
        TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ── Subscription bookkeeping ───────────────────────────────────────────────────────────────────

    private enum StreamKind { Quote, Depth }

    private readonly record struct SubKey(StreamKind Kind, string InstrumentKey);

    private sealed class Subscription
    {
        private readonly Channel<object> _channel =
            Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        public Subscription(StreamKind kind, string instrumentKey)
        {
            Kind = kind;
            InstrumentKey = instrumentKey;
        }

        public StreamKind Kind { get; }
        public string InstrumentKey { get; }
        public ChannelWriter<object> Writer => _channel.Writer;
        public ChannelReader<object> Reader => _channel.Reader;

        public void Complete() => _channel.Writer.TryComplete();
    }
}
