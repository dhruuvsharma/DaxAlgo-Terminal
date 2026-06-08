using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.Binance;

/// <summary>
/// Binance market-data client. Talks to the exchange's <b>public</b> WebSocket + REST endpoints,
/// which need <b>no API key and no account</b> — so it's the zero-credential way to run the
/// terminal against a real, live feed. Each subscription opens its own raw-stream WebSocket
/// (<c>/ws/&lt;stream&gt;</c>) and reconnects with backoff on drop:
/// <list type="bullet">
/// <item>L1 ticks ← <c>@bookTicker</c> (best bid/ask + sizes).</item>
/// <item>L2 depth ← <c>@depth{5|10|20}@100ms</c> (partial-book snapshots — no reconstruction needed).</item>
/// <item>Trade tape ← <c>@trade</c> (price/size + maker flag → aggressor side).</item>
/// <item>Bars ← <c>@kline_&lt;interval&gt;</c> live; history ← REST <c>/api/v3/klines</c>.</item>
/// </list>
/// Crypto quantities are fractional but the canonical size fields are <c>long</c>; every size is
/// multiplied by <see cref="BinanceOptions.SizeScale"/> and rounded (see <see cref="ToSize"/>).
/// This is data-only — there is no order path (the whole build is data/signals only).
/// </summary>
internal sealed class RealBinanceClient : IBrokerClient
{
    private readonly ILogger<RealBinanceClient> _logger;
    private readonly BinanceOptions _options;
    private readonly HttpClient _http = new();
    private readonly BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);

    public RealBinanceClient(ILogger<RealBinanceClient> logger, IOptions<BinanceOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public BrokerKind Kind => BrokerKind.Binance;

    public IObservable<ConnectionState> ConnectionState => _state.AsObservable();

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Connecting);
        try
        {
            // Public data needs no auth handshake; a REST ping just confirms the host is reachable
            // (and not geo-blocked) before we report Connected, so the login form gets real feedback.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await _http.GetAsync($"{_options.RestBaseUrl}/api/v3/ping", cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            _logger.LogInformation("Binance connected — public market data at {Host} (no credentials).", _options.WsBaseUrl);
            _state.OnNext(Core.Domain.ConnectionState.Connected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _state.OnNext(Core.Domain.ConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Binance connect failed reaching {Host}. If it's geo-blocked, point Binance:RestBaseUrl / WsBaseUrl at the Binance.US or data-api.binance.vision hosts.",
                _options.RestBaseUrl);
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
        // Curated list from options — Binance's full universe is ~2k symbols; the picker only needs
        // a sensible default set. Subscriptions still accept any valid Binance symbol.
        IReadOnlyList<TradableInstrument> list = _options.Instruments
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct()
            .Select(s => new TradableInstrument(
                $"{FormatPair(s)}  —  Binance",
                "Crypto (Binance)",
                new Contract(s, "CRYPTO", "BINANCE", QuoteCurrency(s), PrimaryExchange: string.Empty),
                BrokerKind.Binance))
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

        var url = $"{_options.RestBaseUrl}/api/v3/klines?symbol={symbol}&interval={interval}&limit={limit}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bytes);
        return ParseHistoricalKlines(doc.RootElement, _options.SizeScale);
    }

    public IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize, CancellationToken ct = default) =>
        StreamAsync(
            $"{StreamSymbol(contract)}@kline_{MapInterval(barSize)}",
            el => ParseKline(el, _options.SizeScale),
            ct);

    public IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract, CancellationToken ct = default) =>
        StreamAsync(
            $"{StreamSymbol(contract)}@bookTicker",
            el => ParseBookTicker(el, _options.SizeScale),
            ct);

    public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, CancellationToken ct = default) =>
        StreamAsync(
            $"{StreamSymbol(contract)}@depth{BinanceDepthLevels(levels)}@100ms",
            el => ParseDepth(el, _options.SizeScale),
            ct);

    public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(
        Contract contract, CancellationToken ct = default) =>
        StreamAsync(
            $"{StreamSymbol(contract)}@trade",
            ParseTrade,
            ct);

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync().ConfigureAwait(false); } catch { /* swallow */ }
        _http.Dispose();
        _state.Dispose();
    }

    // ── WebSocket plumbing ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a raw single-stream WebSocket, parses each message via <paramref name="parse"/>, and
    /// yields the non-null results. Reconnects with exponential backoff if the socket drops for any
    /// reason other than caller cancellation (Binance closes idle/long-lived connections, ~24h).
    /// </summary>
    private async IAsyncEnumerable<T> StreamAsync<T>(
        string stream,
        Func<JsonElement, T?> parse,
        [EnumeratorCancellation] CancellationToken ct) where T : class
    {
        var uri = new Uri($"{_options.WsBaseUrl}/ws/{stream}");
        var delay = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectInitialDelaySeconds));
        var maxDelay = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectMaxDelaySeconds));

        while (!ct.IsCancellationRequested)
        {
            ClientWebSocket? ws = new();
            try
            {
                await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ws.Dispose();
                yield break;
            }
            catch (Exception ex)
            {
                ws.Dispose();
                ws = null;
                _logger.LogWarning(ex, "Binance WS connect failed for {Stream}; retrying in {Delay}s.", stream, delay.TotalSeconds);
            }

            if (ws is not null)
            {
                delay = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectInitialDelaySeconds)); // reset backoff after a clean connect
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var msg = await ReceiveMessageAsync(ws, ct).ConfigureAwait(false);
                        if (msg is null) break; // close / error → fall out and reconnect

                        T? item = null;
                        try
                        {
                            using var doc = JsonDocument.Parse(msg);
                            item = parse(doc.RootElement);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Binance parse error on {Stream}.", stream);
                        }

                        if (item is not null)
                            yield return item;
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

            if (ct.IsCancellationRequested) yield break;
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));
        }
    }

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

    // ── Parsers (pure + static so they're unit-testable without a network) ──────────────────────

    /// <summary><c>@bookTicker</c> → L1 quote. No event time in the payload, so callers stamp arrival time.</summary>
    internal static Tick? ParseBookTicker(JsonElement el, double sizeScale)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("b", out var bid) || !el.TryGetProperty("a", out var ask)) return null;
        if (!el.TryGetProperty("B", out var bidSz) || !el.TryGetProperty("A", out var askSz)) return null;

        return new Tick(
            DateTime.UtcNow,
            ParseDouble(bid),
            ParseDouble(ask),
            ToSize(ParseDouble(bidSz), sizeScale),
            ToSize(ParseDouble(askSz), sizeScale));
    }

    /// <summary><c>@trade</c> → trade print. <c>m</c> = "buyer is maker": when true the taker (aggressor) is the seller.</summary>
    internal static TradeTick? ParseTrade(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("p", out var price) || !el.TryGetProperty("q", out var qty)) return null;

        var time = el.TryGetProperty("T", out var t) && t.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : DateTime.UtcNow;

        var aggressor = el.TryGetProperty("m", out var maker) && maker.ValueKind == JsonValueKind.True
            ? AggressorSide.Sell // buyer is the maker → seller initiated
            : AggressorSide.Buy;

        // Trade size is intentionally NOT pre-scaled here — ingest's Lee-Ready / footprint logic
        // treats TradeTick.Size as raw units; we keep it raw and only scale on the canonical-size
        // surfaces (quotes/depth). Round fractional crypto qty to the nearest whole unit.
        return new TradeTick(time, ParseDouble(price), (long)Math.Round(ParseDouble(qty)), aggressor);
    }

    /// <summary><c>@depth{N}@100ms</c> partial book → a ready-made snapshot (Binance returns sorted bids desc / asks asc).</summary>
    internal static DepthSnapshot? ParseDepth(JsonElement el, double sizeScale)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("bids", out var bids) || !el.TryGetProperty("asks", out var asks)) return null;

        return new DepthSnapshot(DateTime.UtcNow, ReadLevels(bids, sizeScale), ReadLevels(asks, sizeScale));
    }

    /// <summary><c>@kline_&lt;interval&gt;</c> → bar (emitted on every update, in-progress and closed).</summary>
    internal static Bar? ParseKline(JsonElement el, double sizeScale)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("k", out var k) || k.ValueKind != JsonValueKind.Object)
            return null;

        var openTime = k.TryGetProperty("t", out var t) && t.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : DateTime.UtcNow;

        return new Bar(
            openTime,
            ParseDouble(k.GetProperty("o")),
            ParseDouble(k.GetProperty("h")),
            ParseDouble(k.GetProperty("l")),
            ParseDouble(k.GetProperty("c")),
            ToSize(ParseDouble(k.GetProperty("v")), sizeScale));
    }

    /// <summary>REST <c>/api/v3/klines</c> response (array of arrays) → bars, oldest first.</summary>
    internal static IReadOnlyList<Bar> ParseHistoricalKlines(JsonElement root, double sizeScale)
    {
        var bars = new List<Bar>();
        if (root.ValueKind != JsonValueKind.Array) return bars;

        foreach (var row in root.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6) continue;
            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(row[0].GetInt64()).UtcDateTime;
            bars.Add(new Bar(
                openTime,
                ParseDouble(row[1]),
                ParseDouble(row[2]),
                ParseDouble(row[3]),
                ParseDouble(row[4]),
                ToSize(ParseDouble(row[5]), sizeScale)));
        }
        return bars;
    }

    private static IReadOnlyList<DepthLevel> ReadLevels(JsonElement side, double sizeScale)
    {
        var levels = new List<DepthLevel>();
        if (side.ValueKind != JsonValueKind.Array) return levels;
        foreach (var lvl in side.EnumerateArray())
        {
            if (lvl.ValueKind != JsonValueKind.Array || lvl.GetArrayLength() < 2) continue;
            levels.Add(new DepthLevel(ParseDouble(lvl[0]), ToSize(ParseDouble(lvl[1]), sizeScale)));
        }
        return levels;
    }

    // ── Symbol / value helpers ──────────────────────────────────────────────────────────────────

    private static string StreamSymbol(Contract contract) => contract.Symbol.Trim().ToLowerInvariant();

    /// <summary>Binance order-book partial-depth streams only exist for 5, 10, and 20 levels.</summary>
    internal static int BinanceDepthLevels(int requested) => requested <= 5 ? 5 : requested <= 10 ? 10 : 20;

    internal static string MapInterval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1m",
        BarSize.ThreeMinutes => "3m",
        BarSize.FiveMinutes => "5m",
        BarSize.FifteenMinutes => "15m",
        BarSize.OneHour => "1h",
        BarSize.OneDay => "1d",
        _ => "1m",
    };

    private static long ToSize(double qty, double sizeScale) => (long)Math.Round(qty * sizeScale);

    private static double ParseDouble(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0,
            JsonValueKind.Number => el.GetDouble(),
            _ => 0,
        };

    /// <summary>Best-effort "BTCUSDT" → "BTC/USDT" for display only.</summary>
    private static string FormatPair(string symbol)
    {
        var quote = QuoteCurrency(symbol);
        return symbol.Length > quote.Length && symbol.EndsWith(quote, StringComparison.Ordinal)
            ? $"{symbol[..^quote.Length]}/{quote}"
            : symbol;
    }

    private static string QuoteCurrency(string symbol)
    {
        foreach (var quote in new[] { "USDT", "FDUSD", "USDC", "TUSD", "BUSD", "USD", "BTC", "ETH", "BNB", "EUR", "TRY" })
            if (symbol.EndsWith(quote, StringComparison.Ordinal)) return quote;
        return "USDT";
    }
}
