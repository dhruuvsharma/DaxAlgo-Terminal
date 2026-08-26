using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.Oanda;

/// <summary>
/// OANDA v20 market data — instruments, candles and the pricing stream.
///
/// <para>Written against the published v20 reference rather than from memory: the paths, the query
/// parameters, the granularity codes and the JSON field names below are the documented ones. The parts
/// worth knowing before reading the code:</para>
///
/// <list type="bullet">
///   <item><b>Two hosts, not one.</b> Trading and streaming are separate machines
///     (<c>api-*</c> and <c>stream-*</c>), and practice and live are separate again. A practice token
///     against the live host returns 401, which reads as a bad token rather than as the wrong
///     environment — so the mismatch is called out explicitly when it happens.</item>
///   <item><b>Everything is scoped to an account.</b> Even pricing: the path is
///     <c>/v3/accounts/{id}/pricing/stream</c>. There is no account-free market-data endpoint, so
///     without an account id this client cannot do anything and says so at connect.</item>
///   <item><b>The stream is newline-delimited JSON over a long-lived response</b>, not a WebSocket,
///     and it interleaves <c>HEARTBEAT</c> objects with <c>PRICE</c> ones. A heartbeat is not a
///     quote; treating it as one puts a zero-priced tick into the pipeline.</item>
/// </list>
///
/// <para>Data only. Order routing is a separate contract and a separate set of live-money gates.</para>
/// </summary>
internal sealed class RealOandaClient : IBrokerClient
{
    private readonly ILogger<RealOandaClient> _logger;
    private readonly OandaOptions _options;
    private readonly IOandaTokenSource _tokens;
    private readonly HttpClient _http = new();
    private readonly System.Reactive.Subjects.BehaviorSubject<ConnectionState> _state =
        new(Core.Domain.ConnectionState.Disconnected);

    public RealOandaClient(
        ILogger<RealOandaClient> logger, IOptions<OandaOptions> options, IOandaTokenSource tokens)
    {
        _logger = logger;
        _options = options.Value;
        _tokens = tokens;
    }

    public BrokerKind Kind => BrokerKind.Oanda;

    public IObservable<ConnectionState> ConnectionState =>
        System.Reactive.Linq.Observable.AsObservable(_state);

    // ── connect ─────────────────────────────────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Connecting);

        var token = _tokens.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogError("OANDA needs a personal access token. Add one in the login form.");
            _state.OnNext(Core.Domain.ConnectionState.Failed);
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.AccountId))
        {
            // Worth its own message: every path here is account-scoped, so this is not a detail the
            // user can leave for later.
            _logger.LogError(
                "OANDA needs an account id (like 001-001-1234567-001). Every v20 pricing and candle "
                + "path is scoped to an account.");
            _state.OnNext(Core.Domain.ConnectionState.Failed);
            return;
        }

        try
        {
            using var request = Get($"{_options.RestBaseUrl}/v3/accounts");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // The single most common v20 setup mistake, named rather than reported as "401".
                _logger.LogError(
                    "OANDA rejected the token against the {Environment} host. A token issued for the "
                    + "other environment fails exactly like an invalid one — check whether this is a "
                    + "practice or a live token.",
                    _options.Practice ? "practice" : "live");
                _state.OnNext(Core.Domain.ConnectionState.Failed);
                return;
            }

            response.EnsureSuccessStatusCode();
            _logger.LogInformation(
                "OANDA connected — {Environment} environment, account {Account}.",
                _options.Practice ? "practice" : "live", _options.AccountId);
            _state.OnNext(Core.Domain.ConnectionState.Connected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _state.OnNext(Core.Domain.ConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OANDA connect failed reaching {Host}.", _options.RestBaseUrl);
            _state.OnNext(Core.Domain.ConnectionState.Failed);
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    // ── instruments ─────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default)
    {
        // Configured list wins, because an account can trade hundreds and a picker with hundreds of
        // rows is a picker nobody uses.
        if (_options.Instruments.Length > 0)
        {
            return [.. _options.Instruments
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => Instrument(name.Trim(), name.Trim().Replace('_', '/')))];
        }

        try
        {
            using var request = Get($"{_options.RestBaseUrl}/v3/accounts/{_options.AccountId}/instruments");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            if (!json.RootElement.TryGetProperty("instruments", out var instruments))
                return [];

            var found = new List<TradableInstrument>();
            foreach (var element in instruments.EnumerateArray())
            {
                var name = element.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var display = element.TryGetProperty("displayName", out var d)
                    ? d.GetString() ?? name
                    : name;

                found.Add(Instrument(name!, display));
            }

            return found;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OANDA instrument list failed; offering nothing rather than guessing.");
            return [];
        }
    }

    private static TradableInstrument Instrument(string name, string display) =>
        new($"{display}  —  OANDA",
            "Forex (OANDA)",
            new Contract(name, "CASH", "OANDA", QuoteOf(name), PrimaryExchange: string.Empty),
            BrokerKind.Oanda);

    /// <summary>The quote currency of an OANDA instrument name — the half after the underscore in
    /// <c>EUR_USD</c>. Non-currency instruments (indices, metals) are quoted in the same place.</summary>
    private static string QuoteOf(string name)
    {
        var underscore = name.IndexOf('_');
        return underscore >= 0 && underscore < name.Length - 1 ? name[(underscore + 1)..] : "USD";
    }

    // ── candles ─────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var granularity = Granularity(barSize);
        var count = Math.Clamp((int)(duration / barSize.ToTimeSpan()), 1, Math.Min(_options.MaxCandles, 5000));

        try
        {
            var url = $"{_options.RestBaseUrl}/v3/accounts/{_options.AccountId}/instruments/"
                + $"{Uri.EscapeDataString(contract.Symbol)}/candles"
                + $"?granularity={granularity}&count={count}&price={_options.CandlePrice}";

            using var request = Get(url);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            if (!json.RootElement.TryGetProperty("candles", out var candles)) return [];

            var bars = new List<Bar>();
            foreach (var candle in candles.EnumerateArray())
            {
                // An incomplete candle is the one still forming. Including it in history means the last
                // bar changes under the strategy between one request and the next.
                if (candle.TryGetProperty("complete", out var complete) && !complete.GetBoolean()) continue;
                if (Bar(candle) is { } bar) bars.Add(bar);
            }

            return bars;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OANDA candles failed for {Symbol}.", contract.Symbol);
            return [];
        }
    }

    /// <summary>One candle, read from whichever price side was requested.</summary>
    private Bar? Bar(JsonElement candle)
    {
        var side = _options.CandlePrice.ToUpperInvariant() switch
        {
            "B" => "bid",
            "A" => "ask",
            _ => "mid",
        };

        if (!candle.TryGetProperty(side, out var ohlc)) return null;
        if (!candle.TryGetProperty("time", out var time)) return null;

        // v20 sends prices as STRINGS, not numbers — reading them as doubles throws.
        if (!Price(ohlc, "o", out var open) || !Price(ohlc, "h", out var high)
            || !Price(ohlc, "l", out var low) || !Price(ohlc, "c", out var close))
        {
            return null;
        }

        var volume = candle.TryGetProperty("volume", out var v) && v.TryGetInt64(out var tickCount)
            ? tickCount
            : 0L;

        return new Bar(Timestamp(time.GetString()), open, high, low, close, volume);
    }

    private static bool Price(JsonElement holder, string name, out double value)
    {
        value = 0d;
        if (!holder.TryGetProperty(name, out var element)) return false;

        var text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The documented granularity code for a bar size. Anything without an exact code falls to
    /// the nearest smaller one, because aggregating up is possible and inventing detail is not.</summary>
    private static string Granularity(BarSize size) => size switch
    {
        BarSize.OneMinute => "M1",
        BarSize.ThreeMinutes => "M2",
        BarSize.FiveMinutes => "M5",
        BarSize.FifteenMinutes => "M15",
        BarSize.OneHour => "H1",
        BarSize.OneDay => "D",
        _ => "M1",
    };

    private static DateTime Timestamp(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTime.UtcNow;

    // ── streaming ───────────────────────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var delay = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectInitialDelaySeconds));
        var ceiling = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectMaxDelaySeconds));

        while (!ct.IsCancellationRequested)
        {
            var opened = false;

            await foreach (var tick in StreamAsync(contract, ct).ConfigureAwait(false))
            {
                opened = true;
                delay = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectInitialDelaySeconds));
                yield return tick;
            }

            if (ct.IsCancellationRequested) yield break;

            // A stream that carried data before dropping gets the short delay back; one that never
            // opened keeps backing off, so a misconfiguration does not hammer the host.
            if (!opened) delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, ceiling.Ticks));

            _logger.LogInformation(
                "OANDA price stream for {Symbol} ended; reconnecting in {Delay}.", contract.Symbol, delay);

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    /// <summary>One pass over the pricing stream. Ends on drop; the caller reconnects.</summary>
    private async IAsyncEnumerable<Tick> StreamAsync(
        Contract contract, [EnumeratorCancellation] CancellationToken ct)
    {
        var url = $"{_options.StreamBaseUrl}/v3/accounts/{_options.AccountId}/pricing/stream"
            + $"?instruments={Uri.EscapeDataString(contract.Symbol)}";

        HttpResponseMessage? response = null;
        Stream? stream = null;
        StreamReader? reader = null;

        try
        {
            using var request = Get(url);
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            reader = new StreamReader(stream);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reader?.Dispose(); stream?.Dispose(); response?.Dispose();
            yield break;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OANDA price stream for {Symbol} could not be opened.", contract.Symbol);
            reader?.Dispose(); stream?.Dispose(); response?.Dispose();
            yield break;
        }

        using (response)
        using (stream)
        using (reader)
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    yield break;
                }

                if (line is null) yield break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (ReadTick(line) is { } tick) yield return tick;
            }
        }
    }

    /// <summary>
    /// One line of the stream, or null when it is not a price.
    ///
    /// <para>The stream interleaves <c>HEARTBEAT</c> objects with <c>PRICE</c> ones to keep the
    /// connection alive. A heartbeat carries no book, so taking it as a quote publishes a zero-priced
    /// tick — which a strategy will happily act on.</para>
    /// </summary>
    private Tick? ReadTick(string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "PRICE", StringComparison.Ordinal)) return null;

            var (bid, bidSize) = Best(root, "bids");
            var (ask, askSize) = Best(root, "asks");
            if (bid <= 0d && ask <= 0d) return null;

            var time = root.TryGetProperty("time", out var timeElement) ? timeElement.GetString() : null;
            return new Tick(Timestamp(time), bid, ask, bidSize, askSize);
        }
        catch (JsonException)
        {
            // A partial line at the tail of a dropped stream. Skipped, not fatal.
            return null;
        }
    }

    /// <summary>The top of one side. Liquidity is the size, and it is the only size v20 gives.</summary>
    private static (double Price, long Size) Best(JsonElement root, string side)
    {
        if (!root.TryGetProperty(side, out var levels) || levels.ValueKind != JsonValueKind.Array)
            return (0d, 0L);

        foreach (var level in levels.EnumerateArray())
        {
            if (!Price(level, "price", out var price)) continue;

            var size = level.TryGetProperty("liquidity", out var liquidity)
                && liquidity.TryGetInt64(out var value) ? value : 0L;
            return (price, size);
        }

        return (0d, 0L);
    }

    // ── not offered ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Bars come from the candles endpoint. v20 has no streaming-candle channel, and building
    /// one from the price stream would produce bars that disagree with the broker's own.</summary>
    public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize, [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogInformation(
            "OANDA has no streaming-candle channel; use historical candles plus the tick stream.");
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    /// <summary>OANDA publishes top of book only, so there is no depth to report. Yielding a
    /// one-level book dressed as depth would be worse than yielding none.</summary>
    public async IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    /// <summary>A dealing desk publishes no public tape — there are no prints to stream.</summary>
    public async IAsyncEnumerable<TradeTick> SubscribeTradesAsync(
        Contract contract, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    // ── plumbing ────────────────────────────────────────────────────────────────────────────────

    private HttpRequestMessage Get(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // v20 dates come back RFC3339 by default; asking for it explicitly keeps parsing stable if the
        // account default is ever changed to UNIX.
        request.Headers.Add("Accept-Datetime-Format", "RFC3339");
        return request;
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _state.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Where the OANDA personal access token comes from.
///
/// <para>A seam so the client never reads a credential store directly — the token lives DPAPI-encrypted
/// beside every other broker secret, and a market-data client has no business knowing that.</para>
/// </summary>
public interface IOandaTokenSource
{
    /// <summary>The token, or empty when none is configured.</summary>
    string Token { get; }

    /// <summary>A source that never has one — what an edition composes when OANDA is not set up.</summary>
    public static IOandaTokenSource None { get; } = new NoToken();

    private sealed class NoToken : IOandaTokenSource
    {
        public string Token => string.Empty;
    }
}
