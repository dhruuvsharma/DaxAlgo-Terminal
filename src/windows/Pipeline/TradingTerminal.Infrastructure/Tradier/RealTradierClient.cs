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
using TradingTerminal.Infrastructure.Brokers;

namespace TradingTerminal.Infrastructure.Tradier;

/// <summary>
/// Tradier market data — US equities and options.
///
/// <para>Written from the published brokerage API reference. Not yet run against a real token, so it is
/// catalogued <c>Unverified</c>: the endpoint paths and the response shapes below are the documented
/// ones, and the first person with a sandbox token will find out whether the documentation and the
/// service agree.</para>
///
/// <para><b>The trap this API sets, and the reason to read the parser carefully:</b> a request for one
/// symbol returns <c>quotes.quote</c> as an <i>object</i>, and a request for several returns it as an
/// <i>array</i>. A parser written for either one alone silently finds nothing in the other — no error,
/// no exception, just an empty chart. Both shapes are handled, and there is a test for each.</para>
///
/// <para>Data only, and polled rather than streamed: Tradier's streaming needs a session token obtained
/// through a separate call, so the honest first implementation polls at a declared interval instead of
/// pretending to be a stream.</para>
/// </summary>
internal sealed class RealTradierClient : IBrokerClient
{
    private readonly ILogger<RealTradierClient> _logger;
    private readonly TradierOptions _options;
    private readonly IBrokerCredentialSource _credentials;
    private readonly HttpClient _http = new();
    private readonly System.Reactive.Subjects.BehaviorSubject<ConnectionState> _state =
        new(Core.Domain.ConnectionState.Disconnected);

    public RealTradierClient(
        ILogger<RealTradierClient> logger, IOptions<TradierOptions> options,
        IBrokerCredentialSource credentials)
    {
        _logger = logger;
        _options = options.Value;
        _credentials = credentials;
    }

    /// <summary>The bearer token, taken fresh each time so a key pasted mid-session is picked up on the
    /// next request rather than at the next restart.</summary>
    private string Token => _credentials.For(BrokerKind.Tradier).Secret;

    public BrokerKind Kind => BrokerKind.Tradier;

    public IObservable<ConnectionState> ConnectionState =>
        System.Reactive.Linq.Observable.AsObservable(_state);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Connecting);

        if (string.IsNullOrWhiteSpace(Token))
        {
            _logger.LogError(
                "Tradier needs an access token. A sandbox token is free and issued immediately at "
                + "developer.tradier.com — no funded account required.");
            _state.OnNext(Core.Domain.ConnectionState.Failed);
            return;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var request = Get($"{_options.BaseUrl}/v1/markets/clock");
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Sandbox and production issue different tokens against different hosts, and the wrong
                // pairing fails exactly like a bad token. Naming it saves the obvious wrong guess.
                _logger.LogError(
                    "Tradier rejected the token against the {Environment} host. Sandbox and production "
                    + "use separate tokens — check which one this is.",
                    _options.Sandbox ? "sandbox" : "production");
                _state.OnNext(Core.Domain.ConnectionState.Failed);
                return;
            }

            response.EnsureSuccessStatusCode();
            _logger.LogInformation(
                "Tradier connected — {Environment} environment.", _options.Sandbox ? "sandbox" : "production");
            _state.OnNext(Core.Domain.ConnectionState.Connected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _state.OnNext(Core.Domain.ConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tradier connect failed reaching {Host}.", _options.BaseUrl);
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
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(symbol => symbol.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Select(symbol => new TradableInstrument(
                $"{symbol}  —  Tradier",
                "US equities (Tradier)",
                new Contract(symbol, "STK", "TRADIER", "USD", PrimaryExchange: string.Empty),
                BrokerKind.Tradier))
            .ToList();

        return Task.FromResult(list);
    }

    // ── history ─────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var end = DateTime.UtcNow.Date;
        var start = end - (duration > TimeSpan.Zero ? duration : TimeSpan.FromDays(30));

        var url = $"{_options.BaseUrl}/v1/markets/history"
            + $"?symbol={Uri.EscapeDataString(contract.Symbol)}"
            + $"&interval={Interval(barSize)}"
            + $"&start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}";

        try
        {
            using var request = Get(url);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var json = JsonDocument.Parse(body);

            // If the venue answered with substance and none of it parsed, that is a shape mismatch
            // rather than a quiet market — and it says so instead of drawing an empty chart.
            return WireFormat.OrWarn(ParseHistory(json.RootElement), body, _logger, "Tradier", "history");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tradier history failed for {Symbol}.", contract.Symbol);
            return [];
        }
    }

    /// <summary>Bars live at <c>history.day</c>. The interval is daily or coarser — this API has no
    /// intraday history, so a minute request is answered with days rather than with nothing.</summary>
    internal static IReadOnlyList<Bar> ParseHistory(JsonElement root)
    {
        if (!root.TryGetProperty("history", out var history)
            || history.ValueKind != JsonValueKind.Object
            || !history.TryGetProperty("day", out var days))
        {
            return [];
        }

        var bars = new List<Bar>();
        foreach (var day in Many(days))
        {
            if (!day.TryGetProperty("date", out var date)) continue;
            if (!DateTime.TryParse(date.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var stamp))
            {
                continue;
            }

            bars.Add(new Bar(
                stamp,
                Number(day, "open"),
                Number(day, "high"),
                Number(day, "low"),
                Number(day, "close"),
                (long)Number(day, "volume")));
        }

        return bars;
    }

    /// <summary>Tradier has no intraday history on this endpoint, so anything below a day is a day.</summary>
    private static string Interval(BarSize size) => size switch
    {
        BarSize.OneDay => "daily",
        _ => "daily",
    };

    // ── quotes ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Polls the quote endpoint at the configured interval.
    ///
    /// <para>Polled rather than streamed, and declared as such. Tradier's stream needs a session token
    /// from a separate call; presenting a poll as a stream would be a chart that updates at a rate
    /// nobody chose and nobody can see.</para>
    /// </summary>
    public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.QuotePollSeconds));
        var watch = new WireFormat.StreamWatch(_logger, "Tradier", "quotes");

        while (!ct.IsCancellationRequested)
        {
            Tick? tick = null;
            try
            {
                var url = $"{_options.BaseUrl}/v1/markets/quotes"
                    + $"?symbols={Uri.EscapeDataString(contract.Symbol)}";

                using var request = Get(url);
                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using var json = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

                tick = ParseQuote(json.RootElement).FirstOrDefault();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tradier quote poll failed for {Symbol}.", contract.Symbol);
            }

            watch.Observe(tick is null ? 0 : 1);
            if (tick is not null) yield return tick;

            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    /// <summary>
    /// Reads <c>quotes.quote</c>, which is an <b>object for one symbol and an array for several</b>.
    ///
    /// <para>This is the trap. A parser written for either shape alone finds nothing in the other, and
    /// finds it silently: no exception, no error field, just an empty result and a chart that never
    /// draws. Both shapes go through the same path here.</para>
    /// </summary>
    internal static IEnumerable<Tick> ParseQuote(JsonElement root)
    {
        if (!root.TryGetProperty("quotes", out var quotes)
            || quotes.ValueKind != JsonValueKind.Object
            || !quotes.TryGetProperty("quote", out var quote))
        {
            yield break;
        }

        foreach (var element in Many(quote))
        {
            var bid = Number(element, "bid");
            var ask = Number(element, "ask");
            if (bid <= 0d && ask <= 0d) continue;

            yield return new Tick(
                element.TryGetProperty("trade_date", out var stamp) && stamp.TryGetInt64(out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                    : DateTime.UtcNow,
                bid,
                ask,
                (long)Number(element, "bidsize"),
                (long)Number(element, "asksize"));
        }
    }

    // ── not offered ─────────────────────────────────────────────────────────────────────────────

    /// <summary>No streaming-candle channel; history plus the quote poll covers it.</summary>
    public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    /// <summary>This API publishes top of book only, so there is no depth to report. A one-level book
    /// dressed as depth would be worse than none.</summary>
    public async IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    /// <summary>Time and sales needs the streaming session this adapter does not yet open.</summary>
    public async IAsyncEnumerable<TradeTick> SubscribeTradesAsync(
        Contract contract, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    // ── plumbing ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every element of <paramref name="value"/>, whether it is an array or the single object
    /// this API sends when there is exactly one of something.</summary>
    private static IEnumerable<JsonElement> Many(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) yield return item;
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            yield return value;
        }
    }

    /// <summary>A number, whether the venue sent it as one or as a string.</summary>
    private static double Number(JsonElement holder, string name)
    {
        if (!holder.TryGetProperty(name, out var element)) return 0d;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String => double.TryParse(
                element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0d,
            _ => 0d,
        };
    }

    private HttpRequestMessage Get(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        // Required. Without it the API answers XML, which parses to nothing and looks like an empty
        // market rather than a missing header.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _state.Dispose();
        return ValueTask.CompletedTask;
    }
}

