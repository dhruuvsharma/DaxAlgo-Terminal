using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Brokers;

namespace TradingTerminal.Infrastructure.Crypto;

/// <summary>
/// What every public crypto venue client does the same way: a REST connect check, the connection
/// state, the instrument list from options, bar history with a rollup for sizes the venue lacks, live
/// bars likewise, and a watch on every stream for frames that arrive but never parse.
///
/// <para>The older venue clients (Binance, Coinbase, Bybit, Kraken, OKX) each spell this out in full.
/// The twelve added on 2026-09-25 derive from here and supply only what is venue-specific: URLs,
/// subscribe messages, parsers, and which bar sizes the venue publishes natively.</para>
///
/// <para>Data only. Order routing lives behind its own seam in <c>src/windows/Execution/</c>.</para>
/// </summary>
internal abstract class PublicCryptoClient<TOptions> : IBrokerClient where TOptions : CryptoVenueOptions
{
    private readonly BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);

    protected PublicCryptoClient(ILogger logger, TOptions options)
    {
        Logger = logger;
        Options = options;
        Http = new HttpClient(new HttpClientHandler
        {
            // HTX answers REST gzipped whether asked or not; the rest are happy either way.
            AutomaticDecompression = DecompressionMethods.All,
        });
        // A bare HttpClient sends no User-Agent, and a few venues' edge filters answer that with a
        // challenge page rather than JSON.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("DaxAlgoTerminal/1.0");
    }

    protected ILogger Logger { get; }

    protected TOptions Options { get; }

    protected HttpClient Http { get; }

    public abstract BrokerKind Kind { get; }

    /// <summary>The venue's name, for logs and the instrument picker.</summary>
    protected abstract string VenueName { get; }

    /// <summary>The exchange code carried on each <see cref="Contract"/>.</summary>
    protected abstract string ExchangeCode { get; }

    /// <summary>An unauthenticated REST path (appended to the REST base) that answers when the venue is
    /// reachable — a server-time endpoint, usually.</summary>
    protected abstract string ConnectCheckPath { get; }

    public IObservable<ConnectionState> ConnectionState => _state.AsObservable();

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Connecting);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await SendWithRetryAsync(() => Http.GetAsync(Options.RestBaseUrl + ConnectCheckPath, cts.Token), cts.Token)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            Logger.LogInformation("{Venue} connected — public market data at {Host} (no credentials).", VenueName, Options.WsBaseUrl);
            _state.OnNext(Core.Domain.ConnectionState.Connected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _state.OnNext(Core.Domain.ConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Venue} connect failed reaching {Host}.", VenueName, Options.RestBaseUrl);
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
        IReadOnlyList<TradableInstrument> list = Options.Instruments
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s => new TradableInstrument(
                $"{s}  —  {VenueName}", $"Crypto ({VenueName})",
                new Contract(s, "CRYPTO", ExchangeCode, QuoteOf(s), PrimaryExchange: string.Empty), Kind))
            .ToList();
        return Task.FromResult(list);
    }

    // ── Bars ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>True when the venue's REST history serves <paramref name="size"/> directly.</summary>
    protected abstract bool HasRestInterval(BarSize size);

    /// <summary>True when the venue's socket streams <paramref name="size"/> candles directly.</summary>
    protected abstract bool HasLiveInterval(BarSize size);

    /// <summary>Fetches up to <paramref name="count"/> bars of a natively served size, in any order.</summary>
    protected abstract Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, int count, CancellationToken ct);

    /// <summary>Streams candle updates of a natively served size.</summary>
    protected abstract IAsyncEnumerable<Bar> StreamBarsAsync(string symbol, BarSize size, CancellationToken ct);

    public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        var symbol = Symbol(contract);
        var step = barSize.ToTimeSpan();
        var count = Math.Clamp((int)Math.Ceiling(duration / step), 1, 1000);

        if (HasRestInterval(barSize))
            return BarRollup.Normalise(await FetchBarsAsync(symbol, barSize, count, ct).ConfigureAwait(false), count);

        // Not served: roll one-minute bars up. Asking for count × (step / 1m) minutes keeps the result
        // the length the caller asked for, within the venue's own page limit.
        var minutes = Math.Clamp(count * (int)(step / TimeSpan.FromMinutes(1)), 1, 1000);
        var oneMinute = await FetchBarsAsync(symbol, BarSize.OneMinute, minutes, ct).ConfigureAwait(false);
        return BarRollup.Normalise(BarRollup.Rollup(oneMinute, step), count);
    }

    public IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, CancellationToken ct = default) =>
        HasLiveInterval(barSize)
            ? StreamBarsAsync(Symbol(contract), barSize, ct)
            : SynthesiseBarsAsync(contract, barSize, ct);

    /// <summary>
    /// Live bars of a size the venue does not stream. By default, one-minute candles rolled up; a venue
    /// with no candle stream at all overrides this to build bars from trades instead.
    /// </summary>
    protected virtual IAsyncEnumerable<Bar> SynthesiseBarsAsync(Contract contract, BarSize size, CancellationToken ct) =>
        RollUpAsync(StreamBarsAsync(Symbol(contract), BarSize.OneMinute, ct), size.ToTimeSpan(), ct);

    private static async IAsyncEnumerable<Bar> RollUpAsync(
        IAsyncEnumerable<Bar> minutes, TimeSpan step, [EnumeratorCancellation] CancellationToken ct)
    {
        var rollup = new LiveBarRollup(step);
        await foreach (var minute in minutes.WithCancellation(ct).ConfigureAwait(false))
            yield return rollup.Push(minute);
    }

    /// <summary>Live bars built from trades, for the venues with no candle stream.</summary>
    protected async IAsyncEnumerable<Bar> BarsFromTradesAsync(
        Contract contract, BarSize size, [EnumeratorCancellation] CancellationToken ct)
    {
        var bars = new LiveTradeBars(size.ToTimeSpan());
        await foreach (var trade in SubscribeTradesAsync(contract, ct).WithCancellation(ct).ConfigureAwait(false))
            yield return bars.Push(trade);
    }

    // ── Streams ───────────────────────────────────────────────────────────────────────────────

    public abstract IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default);

    public abstract IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default);

    public abstract IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default);

    /// <summary>A spec on the configured socket URL and reconnect policy.</summary>
    protected CryptoStreamSpec Spec(params string[] subscribe) => new()
    {
        Name = VenueName,
        Url = CryptoStreamSpec.Fixed(Options.WsBaseUrl),
        Subscribe = subscribe,
        InitialDelaySeconds = Options.ReconnectInitialDelaySeconds,
        MaxDelaySeconds = Options.ReconnectMaxDelaySeconds,
    };

    /// <summary>
    /// A JSON stream with a watch on it: a channel that keeps delivering frames none of which parse is
    /// a renamed field, not a quiet market, and it says so in the log instead of looking like one.
    /// </summary>
    protected IAsyncEnumerable<T> Json<T>(
        CryptoStreamSpec spec, string channel, Func<JsonElement, IEnumerable<T>> parse,
        CancellationToken ct, Func<JsonElement, string?>? reply = null)
    {
        var watch = new WireFormat.StreamWatch(Logger, VenueName, channel);
        return CryptoStream.StreamJsonAsync(spec, root =>
        {
            var items = parse(root).ToList();
            watch.Observe(items.Count);
            return items;
        }, Logger, reply, ct);
    }

    /// <summary>GETs a REST path and hands back the parsed body plus its raw text, for
    /// <see cref="WireFormat.OrWarn{T}"/>.</summary>
    protected async Task<(JsonDocument Doc, string Body)> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(() => Http.GetAsync(url, ct), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{VenueName} answered {(int)resp.StatusCode}: {(body.Length > 200 ? body[..200] : body)}",
                null, resp.StatusCode);
        return (JsonDocument.Parse(body), body);
    }

    /// <summary>
    /// Sends, retrying a <b>transport</b> failure twice. An HTTP answer, error or not, is returned as is.
    ///
    /// <para>Some venues' edges drop a fraction of TLS handshakes outright — Bitstamp resets about half
    /// of them from some networks, within 100 ms, and the next attempt succeeds. Without a retry that
    /// reads as the venue being down.</para>
    /// </summary>
    protected static async Task<HttpResponseMessage> SendWithRetryAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await send().ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null && attempt < 3 && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The symbol as the venue writes it. Trimmed only — case is the venue's business.</summary>
    protected virtual string Symbol(Contract contract) => contract.Symbol.Trim();

    /// <summary>The quote currency, for the contract's currency field.</summary>
    protected virtual string QuoteOf(string symbol)
    {
        foreach (var separator in new[] { '-', '_', ':', '/' })
        {
            var at = symbol.LastIndexOf(separator);
            if (at > 0 && at < symbol.Length - 1) return symbol[(at + 1)..].ToUpperInvariant();
        }

        var upper = symbol.ToUpperInvariant();
        foreach (var quote in new[] { "USDT", "USDC", "UST", "USD", "EUR", "GBP", "BTC", "ETH" })
            if (upper.EndsWith(quote, StringComparison.Ordinal) && upper.Length > quote.Length) return quote;
        return "USD";
    }

    public virtual async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync().ConfigureAwait(false); } catch { }
        Http.Dispose();
        _state.Dispose();
    }
}
