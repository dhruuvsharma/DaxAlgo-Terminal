using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Deribit;

/// <summary>
/// Deribit market data — the venue where crypto options actually trade.
///
/// <para>Everything below was verified against the live venue rather than recalled: the REST shapes by
/// calling the public endpoints, and the WebSocket protocol by connecting and subscribing. Three things
/// are not what a reasonable guess would produce:</para>
///
/// <list type="bullet">
///   <item><b>Candles come back column-oriented.</b> Not an array of bars — six parallel arrays
///     (<c>ticks</c>, <c>open</c>, <c>high</c>, <c>low</c>, <c>close</c>, <c>volume</c>) that have to be
///     zipped by index. Any code written for an array of objects finds nothing and reports an empty
///     history, which looks like a quiet venue rather than a parsing bug.</item>
///   <item><b>The whole API is JSON-RPC</b>, over HTTP and over the socket alike. Payloads live under
///     <c>result</c> for a call and under <c>params.data</c> for a subscription push, and the two are
///     different envelopes on the same connection.</item>
///   <item><b>Book updates carry an action per level</b> — <c>["new"|"change"|"delete", price, size]</c>
///     — rather than a size of zero meaning delete, which is the convention most venues use.</item>
/// </list>
///
/// <para>Data only, and keyless: public market data needs no account here.</para>
/// </summary>
internal sealed class RealDeribitClient : IBrokerClient
{
    private readonly ILogger<RealDeribitClient> _logger;
    private readonly DeribitOptions _options;
    private readonly HttpClient _http = new();
    private readonly System.Reactive.Subjects.BehaviorSubject<ConnectionState> _state =
        new(Core.Domain.ConnectionState.Disconnected);

    public RealDeribitClient(ILogger<RealDeribitClient> logger, IOptions<DeribitOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public BrokerKind Kind => BrokerKind.Deribit;

    public IObservable<ConnectionState> ConnectionState =>
        System.Reactive.Linq.Observable.AsObservable(_state);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Connecting);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await _http
                .GetAsync($"{_options.RestBaseUrl}/public/get_time", cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation(
                "Deribit connected — public market data at {Host} (no credentials).", _options.WsBaseUrl);
            _state.OnNext(Core.Domain.ConnectionState.Connected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _state.OnNext(Core.Domain.ConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deribit connect failed reaching {Host}.", _options.RestBaseUrl);
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
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Select(name => new TradableInstrument(
                $"{name}  —  Deribit",
                "Crypto (Deribit)",
                new Contract(name, "CRYPTO", "DERIBIT", QuoteOf(name), PrimaryExchange: string.Empty),
                BrokerKind.Deribit))
            .ToList();

        return Task.FromResult(list);
    }

    /// <summary>The quote currency of a Deribit instrument name. A perpetual is quoted in USD; a dated
    /// or option instrument names its settlement in the same position.</summary>
    private static string QuoteOf(string name) =>
        name.EndsWith("-PERPETUAL", StringComparison.Ordinal) ? "USD"
        : name.Contains("_USDC", StringComparison.Ordinal) ? "USDC"
        : "USD";

    // ── history ─────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var end = DateTimeOffset.UtcNow;
        var start = end - duration;

        var url = $"{_options.RestBaseUrl}/public/get_tradingview_chart_data"
            + $"?instrument_name={Uri.EscapeDataString(contract.Symbol)}"
            + $"&start_timestamp={start.ToUnixTimeMilliseconds()}"
            + $"&end_timestamp={end.ToUnixTimeMilliseconds()}"
            + $"&resolution={Resolution(barSize)}";

        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            return ParseCandles(json.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deribit candles failed for {Symbol}.", contract.Symbol);
            return [];
        }
    }

    /// <summary>
    /// Zips the six parallel arrays into bars.
    ///
    /// <para>This is the shape that is worth knowing before writing anything: Deribit returns a column
    /// per field, not a row per bar. Code written for the usual array-of-objects finds no candles and
    /// reports an empty history — indistinguishable, from the outside, from a venue with nothing to
    /// say.</para>
    /// </summary>
    internal static IReadOnlyList<Bar> ParseCandles(JsonElement root)
    {
        if (!root.TryGetProperty("result", out var result)) return [];
        if (!result.TryGetProperty("ticks", out var ticks) || ticks.ValueKind != JsonValueKind.Array)
            return [];

        // "status":"no_data" is a legitimate answer for a window with no trading in it.
        if (result.TryGetProperty("status", out var status)
            && !string.Equals(status.GetString(), "ok", StringComparison.Ordinal))
        {
            return [];
        }

        var open = Column(result, "open");
        var high = Column(result, "high");
        var low = Column(result, "low");
        var close = Column(result, "close");
        var volume = Column(result, "volume");

        var count = ticks.GetArrayLength();
        var bars = new List<Bar>(count);

        for (var index = 0; index < count; index++)
        {
            // A short column means the venue sent ragged data; stopping is safer than pairing a price
            // with the wrong bar's timestamp.
            if (index >= open.Length || index >= high.Length || index >= low.Length
                || index >= close.Length || index >= volume.Length)
            {
                break;
            }

            var time = DateTimeOffset.FromUnixTimeMilliseconds(ticks[index].GetInt64()).UtcDateTime;
            bars.Add(new Bar(
                time, open[index], high[index], low[index], close[index], (long)Math.Round(volume[index])));
        }

        return bars;
    }

    private static double[] Column(JsonElement result, string name) =>
        result.TryGetProperty(name, out var column) && column.ValueKind == JsonValueKind.Array
            ? [.. column.EnumerateArray().Select(CryptoConvert.D)]
            : [];

    /// <summary>Deribit resolutions are minute counts as strings, plus <c>1D</c>.</summary>
    private static string Resolution(BarSize size) => size switch
    {
        BarSize.OneMinute => "1",
        BarSize.ThreeMinutes => "3",
        BarSize.FiveMinutes => "5",
        BarSize.FifteenMinutes => "15",
        BarSize.OneHour => "60",
        BarSize.OneDay => "1D",
        _ => "1",
    };

    // ── streams ─────────────────────────────────────────────────────────────────────────────────

    public IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize, CancellationToken ct = default) =>
        Stream($"chart.trades.{Sym(contract)}.{Resolution(barSize)}", ParseChartBar, ct);

    public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
        Stream($"ticker.{Sym(contract)}.{_options.Interval}", ParseTicker, ct);

    public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        Stream($"trades.{Sym(contract)}.{_options.Interval}", ParseTrades, ct);

    public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, CancellationToken ct = default)
    {
        var book = new L2OrderBook();
        return Stream($"book.{Sym(contract)}.{_options.Interval}",
            element => ParseBook(element, book, levels), ct);
    }

    private static string Sym(Contract contract) => contract.Symbol.Trim().ToUpperInvariant();

    private IAsyncEnumerable<T> Stream<T>(
        string channel, Func<JsonElement, IEnumerable<T>> parse, CancellationToken ct) =>
        CryptoStream.StreamAsync(
            _options.WsBaseUrl,
            $$$"""{"jsonrpc":"2.0","id":1,"method":"public/subscribe","params":{"channels":["{{{channel}}}"]}}""",
            parse,
            _options.ReconnectInitialDelaySeconds,
            _options.ReconnectMaxDelaySeconds,
            _logger,
            "Deribit",
            // The venue closes an idle socket. Its own heartbeat is a JSON-RPC call like everything else.
            pingJson: """{"jsonrpc":"2.0","id":0,"method":"public/test"}""",
            pingIntervalSeconds: 20,
            ct: ct);

    // ── parsers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The payload of a subscription push, or nothing.
    ///
    /// <para>A subscribe acknowledgement arrives on the same socket carrying <c>result</c> rather than
    /// <c>params</c>, so every parser has to distinguish them — reading the acknowledgement as data is
    /// how a stream publishes one garbage tick per reconnect.</para>
    /// </summary>
    private static bool TryData(JsonElement element, string channelPrefix, out JsonElement data)
    {
        data = default;

        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("method", out var method)
            && string.Equals(method.GetString(), "subscription", StringComparison.Ordinal)
            && element.TryGetProperty("params", out var parameters)
            && parameters.TryGetProperty("channel", out var channel)
            && (channel.GetString() ?? string.Empty).StartsWith(channelPrefix, StringComparison.Ordinal)
            && parameters.TryGetProperty("data", out data);
    }

    internal static IEnumerable<Tick> ParseTicker(JsonElement element)
    {
        if (!TryData(element, "ticker.", out var data) || data.ValueKind != JsonValueKind.Object)
            yield break;

        var bid = CryptoConvert.D(data, "best_bid_price");
        var ask = CryptoConvert.D(data, "best_ask_price");
        if (bid <= 0d && ask <= 0d) yield break;

        // Deribit's amounts are already contract counts, so they are whole numbers rather than the
        // fractional base quantities most spot venues send — no size scaling is wanted here.
        yield return new Tick(
            Time(data),
            bid,
            ask,
            (long)Math.Round(CryptoConvert.D(data, "best_bid_amount")),
            (long)Math.Round(CryptoConvert.D(data, "best_ask_amount")));
    }

    internal static IEnumerable<TradeTick> ParseTrades(JsonElement element)
    {
        if (!TryData(element, "trades.", out var data) || data.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var trade in data.EnumerateArray())
        {
            var price = CryptoConvert.D(trade, "price");
            if (price <= 0d) continue;

            // "direction" is the aggressor, which is what a tape wants — who crossed the spread.
            var side = trade.TryGetProperty("direction", out var direction)
                && string.Equals(direction.GetString(), "sell", StringComparison.Ordinal)
                ? AggressorSide.Sell
                : AggressorSide.Buy;

            yield return new TradeTick(
                Time(trade), price, (long)Math.Round(CryptoConvert.D(trade, "amount")), side);
        }
    }

    internal static IEnumerable<Bar> ParseChartBar(JsonElement element)
    {
        if (!TryData(element, "chart.trades.", out var data) || data.ValueKind != JsonValueKind.Object)
            yield break;

        var open = CryptoConvert.D(data, "open");
        if (open <= 0d) yield break;

        yield return new Bar(
            data.TryGetProperty("tick", out var tick) && tick.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                : DateTime.UtcNow,
            open,
            CryptoConvert.D(data, "high"),
            CryptoConvert.D(data, "low"),
            CryptoConvert.D(data, "close"),
            (long)Math.Round(CryptoConvert.D(data, "volume")));
    }

    /// <summary>
    /// Applies a book message.
    ///
    /// <para>Deribit states the action per level — <c>["new"|"change"|"delete", price, size]</c> —
    /// rather than using a size of zero to mean removal, which is what most venues do. Treating a
    /// delete as a size update leaves the level in the book at its old size, and the top of book
    /// slowly fills with prices that are no longer there.</para>
    /// </summary>
    internal static IEnumerable<DepthSnapshot> ParseBook(JsonElement element, L2OrderBook book, int levels)
    {
        if (!TryData(element, "book.", out var data) || data.ValueKind != JsonValueKind.Object)
            yield break;

        // A snapshot replaces the book; an update amends it. Amending onto a stale book is how two
        // sides of a spread end up crossed.
        if (data.TryGetProperty("type", out var type)
            && string.Equals(type.GetString(), "snapshot", StringComparison.Ordinal))
        {
            book.Clear();
        }

        Apply(data, "bids", isBid: true);
        Apply(data, "asks", isBid: false);

        if (book.IsEmpty) yield break;
        yield return book.Snapshot(levels, sizeScale: 1d, Time(data));

        void Apply(JsonElement holder, string name, bool isBid)
        {
            if (!holder.TryGetProperty(name, out var side) || side.ValueKind != JsonValueKind.Array)
                return;

            foreach (var level in side.EnumerateArray())
            {
                if (level.ValueKind != JsonValueKind.Array || level.GetArrayLength() < 3) continue;

                var action = level[0].GetString();
                var price = CryptoConvert.D(level[1]);
                var size = string.Equals(action, "delete", StringComparison.Ordinal)
                    ? 0d
                    : CryptoConvert.D(level[2]);

                book.Apply(isBid, price, size);
            }
        }
    }

    private static DateTime Time(JsonElement holder) =>
        holder.TryGetProperty("timestamp", out var timestamp) && timestamp.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : DateTime.UtcNow;

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _state.Dispose();
        return ValueTask.CompletedTask;
    }
}
