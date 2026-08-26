using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Hyperliquid;

/// <summary>
/// Hyperliquid market data — the perpetuals DEX.
///
/// <para>Verified against the live venue: the REST shapes by posting to the info endpoint, the socket
/// by connecting and subscribing. Four things differ from every other venue in this tree, and none of
/// them are guessable:</para>
///
/// <list type="bullet">
///   <item><b>The book has no bid and ask keys.</b> <c>levels</c> is a two-element array —
///     <c>levels[0]</c> is bids, <c>levels[1]</c> is asks — identified by position alone. Read it as an
///     object and there is nothing to find.</item>
///   <item><b>There is no ticker channel.</b> Top of book is derived from the first level of each side
///     of the book, so an L1 subscription here is an L2 subscription that publishes only the touch.</item>
///   <item><b>Every number is a string</b>, prices and sizes alike.</item>
///   <item><b>Reading is a POST.</b> One URL, a <c>type</c> in the body, no per-resource paths — so the
///     usual GET-with-query shape does not apply anywhere.</item>
/// </list>
///
/// <para>A trade's side is <c>B</c> or <c>A</c> — the side of the book that was hit — rather than the
/// <c>buy</c>/<c>sell</c> spelling everywhere else.</para>
/// </summary>
internal sealed class RealHyperliquidClient : IBrokerClient
{
    private readonly ILogger<RealHyperliquidClient> _logger;
    private readonly HyperliquidOptions _options;
    private readonly HttpClient _http = new();
    private readonly System.Reactive.Subjects.BehaviorSubject<ConnectionState> _state =
        new(Core.Domain.ConnectionState.Disconnected);

    public RealHyperliquidClient(ILogger<RealHyperliquidClient> logger, IOptions<HyperliquidOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public BrokerKind Kind => BrokerKind.Hyperliquid;

    public IObservable<ConnectionState> ConnectionState =>
        System.Reactive.Linq.Observable.AsObservable(_state);

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Connecting);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await PostAsync("""{"type":"meta"}""", cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation(
                "Hyperliquid connected — public market data at {Host} (no credentials).", _options.WsBaseUrl);
            _state.OnNext(Core.Domain.ConnectionState.Connected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _state.OnNext(Core.Domain.ConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hyperliquid connect failed reaching {Host}.", _options.RestBaseUrl);
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
            .Where(coin => !string.IsNullOrWhiteSpace(coin))
            .Select(coin => coin.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Select(coin => new TradableInstrument(
                $"{coin}-PERP  —  Hyperliquid",
                "Crypto (Hyperliquid)",
                // The venue names a perpetual by its base asset alone; the display adds -PERP for a
                // reader, but the wire symbol must stay the bare coin.
                new Contract(coin, "CRYPTO", "HYPERLIQUID", "USDC", PrimaryExchange: string.Empty),
                BrokerKind.Hyperliquid))
            .ToList();

        return Task.FromResult(list);
    }

    // ── history ─────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var end = DateTimeOffset.UtcNow;
        var start = end - duration;

        // Serialised rather than interpolated. The request is a nested object, so hand-built JSON here
        // would be a string with three closing braces in it — the shape most likely to be quietly
        // malformed, and the venue answers malformed JSON the same way it answers a bad symbol.
        var body = JsonSerializer.Serialize(new
        {
            type = "candleSnapshot",
            req = new
            {
                coin = Sym(contract),
                interval = Interval(barSize),
                startTime = start.ToUnixTimeMilliseconds(),
                endTime = end.ToUnixTimeMilliseconds(),
            },
        });

        try
        {
            using var response = await PostAsync(body, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            return ParseCandles(json.RootElement, _options.SizeScale);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hyperliquid candles failed for {Symbol}.", contract.Symbol);
            return [];
        }
    }

    /// <summary>Candles are a flat array; <c>t</c> opens the bar and <c>T</c> closes it.</summary>
    internal static IReadOnlyList<Bar> ParseCandles(JsonElement root, double sizeScale)
    {
        if (root.ValueKind != JsonValueKind.Array) return [];

        var bars = new List<Bar>(root.GetArrayLength());
        foreach (var candle in root.EnumerateArray())
        {
            if (candle.ValueKind != JsonValueKind.Object) continue;

            var open = CryptoConvert.D(candle, "o");
            if (open <= 0d) continue;

            bars.Add(new Bar(
                candle.TryGetProperty("t", out var t) && t.TryGetInt64(out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                    : DateTime.UtcNow,
                open,
                CryptoConvert.D(candle, "h"),
                CryptoConvert.D(candle, "l"),
                CryptoConvert.D(candle, "c"),
                CryptoConvert.ToSize(CryptoConvert.D(candle, "v"), sizeScale)));
        }

        return bars;
    }

    private static string Interval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1m",
        BarSize.ThreeMinutes => "3m",
        BarSize.FiveMinutes => "5m",
        BarSize.FifteenMinutes => "15m",
        BarSize.OneHour => "1h",
        BarSize.OneDay => "1d",
        _ => "1m",
    };

    // ── streams ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Top of book, derived from the book itself.
    ///
    /// <para>The venue publishes no ticker channel, so an L1 subscription here is an L2 subscription
    /// that yields only the touch. Saying so is better than leaving the method empty: a chart that
    /// wants a quote gets one, and it is the same number the book's first level carries.</para>
    /// </summary>
    public IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default) =>
        Stream("l2Book", Sym(contract), element => ParseTouch(element, _options.SizeScale), ct);

    public IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, CancellationToken ct = default) =>
        Stream("trades", Sym(contract), element => ParseTrades(element, _options.SizeScale), ct);

    public IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, CancellationToken ct = default) =>
        Stream("l2Book", Sym(contract),
            element => ParseBook(element, levels, _options.SizeScale), ct);

    /// <summary>No candle channel is offered; history plus the tape covers it.</summary>
    public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    private static string Sym(Contract contract) => contract.Symbol.Trim().ToUpperInvariant();

    private IAsyncEnumerable<T> Stream<T>(
        string type, string coin, Func<JsonElement, IEnumerable<T>> parse, CancellationToken ct) =>
        CryptoStream.StreamAsync(
            _options.WsBaseUrl,
            $$$"""{"method":"subscribe","subscription":{"type":"{{{type}}}","coin":"{{{coin}}}"}}""",
            parse,
            _options.ReconnectInitialDelaySeconds,
            _options.ReconnectMaxDelaySeconds,
            _logger,
            "Hyperliquid",
            pingJson: """{"method":"ping"}""",
            pingIntervalSeconds: 30,
            ct: ct);

    // ── parsers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The payload of a push, or nothing.
    ///
    /// <para>The venue answers a subscribe with a <c>subscriptionResponse</c> on the same socket. Read
    /// as data it yields one meaningless value per reconnect, and reconnects are routine.</para>
    /// </summary>
    private static bool TryData(JsonElement element, string channel, out JsonElement data)
    {
        data = default;

        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("channel", out var name)
            && string.Equals(name.GetString(), channel, StringComparison.Ordinal)
            && element.TryGetProperty("data", out data);
    }

    /// <summary>
    /// The two sides of the book.
    ///
    /// <para><c>levels</c> is positional: index 0 is bids, index 1 is asks. There are no keys to read,
    /// which is why code written against the usual shape finds an empty book rather than failing.</para>
    /// </summary>
    private static bool TrySides(JsonElement data, out JsonElement bids, out JsonElement asks)
    {
        bids = default;
        asks = default;

        if (!data.TryGetProperty("levels", out var levels)
            || levels.ValueKind != JsonValueKind.Array
            || levels.GetArrayLength() < 2)
        {
            return false;
        }

        bids = levels[0];
        asks = levels[1];
        return bids.ValueKind == JsonValueKind.Array && asks.ValueKind == JsonValueKind.Array;
    }

    internal static IEnumerable<Tick> ParseTouch(JsonElement element, double sizeScale)
    {
        if (!TryData(element, "l2Book", out var data) || !TrySides(data, out var bids, out var asks))
            yield break;

        if (bids.GetArrayLength() == 0 || asks.GetArrayLength() == 0) yield break;

        var bid = bids[0];
        var ask = asks[0];

        yield return new Tick(
            Time(data),
            CryptoConvert.D(bid, "px"),
            CryptoConvert.D(ask, "px"),
            CryptoConvert.ToSize(CryptoConvert.D(bid, "sz"), sizeScale),
            CryptoConvert.ToSize(CryptoConvert.D(ask, "sz"), sizeScale));
    }

    internal static IEnumerable<DepthSnapshot> ParseBook(JsonElement element, int levels, double sizeScale)
    {
        if (!TryData(element, "l2Book", out var data) || !TrySides(data, out var bids, out var asks))
            yield break;

        // Every push is a full snapshot of the top of book, so the book is rebuilt rather than amended
        // — there is no incremental state to keep and nothing to fall out of sync.
        var book = new L2OrderBook();
        Apply(bids, isBid: true);
        Apply(asks, isBid: false);

        if (book.IsEmpty) yield break;
        yield return book.Snapshot(levels, sizeScale, Time(data));

        void Apply(JsonElement side, bool isBid)
        {
            foreach (var level in side.EnumerateArray())
            {
                var price = CryptoConvert.D(level, "px");
                if (price > 0d) book.Apply(isBid, price, CryptoConvert.D(level, "sz"));
            }
        }
    }

    internal static IEnumerable<TradeTick> ParseTrades(JsonElement element, double sizeScale)
    {
        if (!TryData(element, "trades", out var data) || data.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var trade in data.EnumerateArray())
        {
            var price = CryptoConvert.D(trade, "px");
            if (price <= 0d) continue;

            // "B" and "A" name the side of the book that was hit, not the words every other venue uses.
            var side = trade.TryGetProperty("side", out var s)
                && string.Equals(s.GetString(), "A", StringComparison.Ordinal)
                ? AggressorSide.Sell
                : AggressorSide.Buy;

            yield return new TradeTick(
                trade.TryGetProperty("time", out var t) && t.TryGetInt64(out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                    : DateTime.UtcNow,
                price,
                CryptoConvert.ToSize(CryptoConvert.D(trade, "sz"), sizeScale),
                side);
        }
    }

    private static DateTime Time(JsonElement holder) =>
        holder.TryGetProperty("time", out var time) && time.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : DateTime.UtcNow;

    private Task<HttpResponseMessage> PostAsync(string body, CancellationToken ct) =>
        _http.PostAsync(
            $"{_options.RestBaseUrl}/info",
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct);

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _state.Dispose();
        return ValueTask.CompletedTask;
    }
}
