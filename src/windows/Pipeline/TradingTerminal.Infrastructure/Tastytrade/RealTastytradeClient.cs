using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Tastytrade;

/// <summary>One DXLink event, reduced to what the terminal uses.</summary>
internal sealed record DxEvent(string Type, string Symbol, Tick? Quote = null, TradeTick? Trade = null, Bar? Candle = null, int Flags = 0);

/// <summary>
/// tastytrade: an OAuth 2 session for its REST API, and market data over DXLink — dxFeed's WebSocket
/// protocol, reached with a quote token the REST API issues.
///
/// <para><b>Session.</b> A personal OAuth grant: the user creates one on tastytrade's site and pastes its
/// refresh token, which never expires; the keeper trades it for 15-minute access tokens.</para>
///
/// <para><b>What DXLink gives.</b> <c>Quote</c> (the touch), <c>TimeAndSale</c> — real prints with the
/// aggressor side, so this is one of the few brokers here with a true trade tape — and <c>Candle</c>, which
/// is also where history comes from: subscribing to <c>AAPL{=5m}</c> from a start time sends every candle
/// since, then keeps the forming one updated. No depth is offered at the API level.</para>
///
/// <para><b>Handshake.</b> SETUP and AUTH go out on connect; the channel is requested once the feed says
/// AUTHORIZED, and subscriptions follow once the channel is open.</para>
///
/// <para>Message shapes are from dxFeed's DXLink protocol description and tastytrade's streaming guide.
/// Written 2026-09-25; not yet run against a real account.</para>
/// </summary>
internal sealed class RealTastytradeClient : KeptSessionClient<TastytradeOptions>
{
    internal const int Channel = 1;

    /// <summary>dxFeed event flags: the end of a snapshot, a snapshot cut short, a removed event.</summary>
    internal const int SnapshotEnd = 0x08, SnapshotSnip = 0x10, RemoveEvent = 0x02;

    internal static readonly string[] QuoteFields = ["eventType", "eventSymbol", "bidPrice", "askPrice", "bidSize", "askSize", "bidTime", "askTime"];
    internal static readonly string[] TradeFields = ["eventType", "eventSymbol", "time", "price", "size", "aggressorSide"];
    internal static readonly string[] CandleFields = ["eventType", "eventSymbol", "time", "open", "high", "low", "close", "volume", "eventFlags"];

    private readonly SharedFeed<DxEvent> _feed;
    private volatile bool _channelOpen;
    private string _token = string.Empty;

    public RealTastytradeClient(
        ILogger<RealTastytradeClient> logger, IOptions<TastytradeOptions> options, IBrokerCredentialSource credentials, IBrokerSessionStore store)
        : base(logger, options.Value, credentials, store, BrokerKind.Tastytrade)
    {
        _feed = new SharedFeed<DxEvent>(new FeedProtocol<DxEvent>
        {
            Name = "tastytrade DXLink",
            Endpoint = OpenFeedAsync,
            OnConnect = _ => [Setup(), Auth(_token)],
            OnAdd = key => _channelOpen ? [Subscription("add", [key])] : [],
            OnRemove = key => _channelOpen ? [Subscription("remove", [key])] : [],
            Reply = Answer,
            Decode = frame => Decode(frame).Select(e => new KeyValuePair<string, DxEvent>(KeyOf(e.Type, e.Symbol), e)),
            Ping = """{"type":"KEEPALIVE","channel":0}""",
            PingSeconds = 30,
        }, logger);
    }

    public override BrokerKind Kind => BrokerKind.Tastytrade;
    protected override string BrokerName => "tastytrade";
    protected override string SignInAdvice => "Sign in to tastytrade in the login window with a personal OAuth grant.";
    protected override string CurrencyOf(string symbol) => "USD";
    protected override string SecTypeOf(string symbol) => symbol.StartsWith('/') ? "FUT" : "STK";
    protected override string ExchangeOf(string symbol) => "SMART";

    protected override Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct) =>
        TastytradeSignIn.RefreshAsync(Http, Options.RestBaseUrl, app, session.RefreshToken, DateTimeOffset.UtcNow, ct);

    protected override async Task CheckSessionAsync(CancellationToken ct) => _ = await QuoteTokenAsync(ct).ConfigureAwait(false);

    /// <summary>The DXLink token and address. Asking for it also proves the account may stream quotes.</summary>
    private async Task<(string Token, string Url)> QuoteTokenAsync(CancellationToken ct)
    {
        var (doc, body) = await GetAsync($"{Options.RestBaseUrl}/api-quote-tokens", ct).ConfigureAwait(false);
        using (doc) return ParseQuoteToken(doc.RootElement) ?? throw new InvalidOperationException($"tastytrade issued no quote token: {body}");
    }

    /// <summary><c>{"data":{"token":"…","dxlink-url":"wss://…","level":"api"}}</c>.</summary>
    internal static (string Token, string Url)? ParseQuoteToken(JsonElement root)
    {
        var token = SignInProof.Text(root, "data.token");
        var url = SignInProof.Text(root, "data.dxlink-url");
        return string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(url) ? null : (token, url);
    }

    private async Task<FeedEndpoint> OpenFeedAsync(CancellationToken ct)
    {
        _channelOpen = false;
        var (token, url) = await QuoteTokenAsync(ct).ConfigureAwait(false);
        _token = token;
        return FeedEndpoint.At(url);
    }

    // ── Protocol ────────────────────────────────────────────────────────────────────────────────

    internal static string KeyOf(string type, string symbol) => type + "|" + symbol;

    internal static string Setup() =>
        """{"type":"SETUP","channel":0,"version":"0.1-DXF-JS/0.3.0","keepaliveTimeout":60,"acceptKeepaliveTimeout":60}""";

    internal static string Auth(string token) => new JsonObject { ["type"] = "AUTH", ["channel"] = 0, ["token"] = token }.ToJsonString();

    internal static string ChannelRequest() =>
        $$$"""{"type":"CHANNEL_REQUEST","channel":{{{Channel}}},"service":"FEED","parameters":{"contract":"AUTO"}}""";

    internal static string FeedSetup() => new JsonObject
    {
        ["type"] = "FEED_SETUP",
        ["channel"] = Channel,
        ["acceptAggregationPeriod"] = 0.1,
        ["acceptDataFormat"] = "COMPACT",
        ["acceptEventFields"] = new JsonObject
        {
            ["Quote"] = new JsonArray([.. QuoteFields.Select(f => (JsonNode)f)]),
            ["TimeAndSale"] = new JsonArray([.. TradeFields.Select(f => (JsonNode)f)]),
            ["Candle"] = new JsonArray([.. CandleFields.Select(f => (JsonNode)f)]),
        },
    }.ToJsonString();

    /// <summary>An add or remove for keys <c>Type|Symbol</c>. A candle subscription starts one bar before the
    /// current one, so the forming bar arrives with the bar it follows.</summary>
    internal static string Subscription(string verb, IEnumerable<string> keys, DateTimeOffset? now = null)
    {
        var items = new JsonArray();
        foreach (var key in keys)
        {
            var bar = key.IndexOf('|');
            var (type, symbol) = (key[..bar], key[(bar + 1)..]);
            var item = new JsonObject { ["type"] = type, ["symbol"] = symbol };
            if (type == "Candle" && verb == "add" && CandleStep(symbol) is { } step)
                item["fromTime"] = new DateTimeOffset(BarRollup.BucketStart((now ?? DateTimeOffset.UtcNow).UtcDateTime - step, step)).ToUnixTimeMilliseconds();
            items.Add(item);
        }

        return new JsonObject { ["type"] = "FEED_SUBSCRIPTION", ["channel"] = Channel, [verb] = items }.ToJsonString();
    }

    /// <summary>A candle subscription with an explicit start, for history.</summary>
    internal static string HistorySubscription(string candleSymbol, DateTimeOffset from) => new JsonObject
    {
        ["type"] = "FEED_SUBSCRIPTION",
        ["channel"] = Channel,
        ["add"] = new JsonArray(new JsonObject { ["type"] = "Candle", ["symbol"] = candleSymbol, ["fromTime"] = from.ToUnixTimeMilliseconds() }),
    }.ToJsonString();

    internal static string CandleSymbol(string symbol, BarSize size) => size switch
    {
        BarSize.OneMinute => $"{symbol}{{=1m}}",
        BarSize.ThreeMinutes => $"{symbol}{{=3m}}",
        BarSize.FiveMinutes => $"{symbol}{{=5m}}",
        BarSize.FifteenMinutes => $"{symbol}{{=15m}}",
        BarSize.OneHour => $"{symbol}{{=1h}}",
        _ => $"{symbol}{{=1d}}",
    };

    internal static TimeSpan? CandleStep(string candleSymbol)
    {
        var open = candleSymbol.IndexOf("{=", StringComparison.Ordinal);
        if (open < 0) return null;
        var spec = candleSymbol[(open + 2)..].TrimEnd('}');
        if (spec.Length < 2 || !int.TryParse(spec[..^1], out var n)) return null;
        return spec[^1] switch
        {
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => null,
        };
    }

    /// <summary>Answers the handshake: the channel once authorised, the setup and every subscription once it
    /// is open. A refused token is logged in dxFeed's words.</summary>
    private IEnumerable<string> Answer(byte[] frame, IReadOnlyCollection<string> keys)
    {
        var step = HandshakeStep(frame);
        switch (step)
        {
            case "AUTHORIZED":
                return [ChannelRequest()];
            case "CHANNEL_OPENED":
                _channelOpen = true;
                return keys.Count == 0 ? [FeedSetup()] : [FeedSetup(), Subscription("add", keys)];
            case { } error when error.StartsWith("ERROR", StringComparison.Ordinal):
                Logger.LogWarning("tastytrade DXLink: {Error}", error);
                return [];
            default:
                return [];
        }
    }

    /// <summary><c>AUTHORIZED</c>, <c>CHANNEL_OPENED</c>, <c>ERROR: …</c>, or null for anything else.</summary>
    internal static string? HandshakeStep(byte[] frame)
    {
        if (frame.AsSpan().IndexOf("FEED_DATA"u8) >= 0) return null;
        using var doc = JsonDocument.Parse(frame);
        var root = doc.RootElement;
        return SignInProof.Text(root, "type") switch
        {
            "AUTH_STATE" when SignInProof.Text(root, "state") == "AUTHORIZED" => "AUTHORIZED",
            "CHANNEL_OPENED" => "CHANNEL_OPENED",
            "ERROR" => $"ERROR: {SignInProof.Text(root, "error")} {SignInProof.Text(root, "message")}",
            _ => null,
        };
    }

    /// <summary>
    /// <c>{"type":"FEED_DATA","channel":1,"data":["Quote",[flat values…],"Candle",[…]]}</c> — COMPACT format:
    /// each type is followed by the values of all its events laid end to end, in the field order the
    /// FEED_SETUP asked for. dxFeed writes a missing number as <c>"NaN"</c>.
    /// </summary>
    internal static IReadOnlyList<DxEvent> Decode(byte[] frame)
    {
        var events = new List<DxEvent>();
        if (frame.AsSpan().IndexOf("FEED_DATA"u8) < 0) return events;
        using var doc = JsonDocument.Parse(frame);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return events;

        string? type = null;
        foreach (var part in data.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                type = part.GetString();
                continue;
            }

            if (part.ValueKind != JsonValueKind.Array || type is null) continue;
            var width = type switch { "Quote" => QuoteFields.Length, "TimeAndSale" => TradeFields.Length, "Candle" => CandleFields.Length, _ => 0 };
            if (width == 0) continue;

            var values = part.EnumerateArray().ToList();
            for (var i = 0; i + width <= values.Count; i += width)
                if (Event(type, values, i) is { } e) events.Add(e);
        }

        return events;
    }

    private static DxEvent? Event(string type, List<JsonElement> v, int at)
    {
        var symbol = v[at + 1].ValueKind == JsonValueKind.String ? v[at + 1].GetString()! : string.Empty;
        double N(int i) => CryptoConvert.D(v[at + i]) is var d && double.IsFinite(d) ? d : 0;

        switch (type)
        {
            case "Quote":
            {
                var (bid, ask) = (N(2), N(3));
                if (bid <= 0 || ask <= 0) return null;
                var time = Math.Max((long)N(6), (long)N(7));
                return new DxEvent(type, symbol, Quote: new Tick(CryptoConvert.MsUtc(time), bid, ask, (long)N(4), (long)N(5)));
            }

            case "TimeAndSale":
            {
                var price = N(3);
                if (price <= 0) return null;
                var side = v[at + 5].ValueKind == JsonValueKind.String ? v[at + 5].GetString() : null;
                var aggressor = side switch { "BUY" => AggressorSide.Buy, "SELL" => AggressorSide.Sell, _ => AggressorSide.Unknown };
                return new DxEvent(type, symbol, Trade: new TradeTick(CryptoConvert.MsUtc((long)N(2)), price, (long)N(4), aggressor));
            }

            case "Candle":
            {
                var flags = (int)N(8);
                var (open, high, low, close) = (N(3), N(4), N(5), N(6));
                var candle = open > 0 && close > 0 ? new Bar(CryptoConvert.MsUtc((long)N(2)), open, high, low, close, (long)N(7)) : null;
                return new DxEvent(type, symbol, Candle: candle, Flags: flags);
            }

            default:
                return null;
        }
    }

    // ── Streams ─────────────────────────────────────────────────────────────────────────────────

    public override async IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var e in _feed.SubscribeAsync(KeyOf("Quote", Symbol(contract)), ct).ConfigureAwait(false))
            if (e.Quote is { } quote) yield return quote;
    }

    public override async IAsyncEnumerable<TradeTick> SubscribeTradesAsync(Contract contract, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var e in _feed.SubscribeAsync(KeyOf("TimeAndSale", Symbol(contract)), ct).ConfigureAwait(false))
            if (e.Trade is { } trade) yield return trade;
    }

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default) =>
        throw new NotSupportedException("tastytrade's API quote level carries the touch only — no order-book depth.");

    public override async IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Bar? latest = null;
        await foreach (var e in _feed.SubscribeAsync(KeyOf("Candle", CandleSymbol(Symbol(contract), barSize)), ct).ConfigureAwait(false))
        {
            // Older candles arrive with the forming one; only the newest moves the chart.
            if (e.Candle is not { } bar || (e.Flags & RemoveEvent) != 0 || (latest is not null && bar.TimestampUtc < latest.TimestampUtc)) continue;
            latest = bar;
            yield return bar;
        }
    }

    // ── History ─────────────────────────────────────────────────────────────────────────────────

    protected override bool HasInterval(BarSize size) => true;

    /// <summary>
    /// History over its own short-lived DXLink connection: subscribe to the candle symbol from the start
    /// time, collect until dxFeed marks the snapshot complete (or goes quiet), close. A separate socket
    /// because a candle subscription's start time is fixed when it is made — sharing the live one would
    /// either miss history or replay it into the live chart.
    /// </summary>
    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, TimeSpan span, CancellationToken ct)
    {
        var (token, url) = await QuoteTokenAsync(ct).ConfigureAwait(false);
        var candleSymbol = CandleSymbol(symbol, size);
        var from = DateTimeOffset.UtcNow - span - size.ToTimeSpan();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, Options.HistoryTimeoutSeconds)));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(url), deadline.Token).ConfigureAwait(false);

        async Task Send(string text) =>
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);

        await Send(Setup()).ConfigureAwait(false);
        await Send(Auth(token)).ConfigureAwait(false);

        var bars = new Dictionary<DateTime, Bar>();
        try
        {
            while (true)
            {
                var frame = await ReceiveAsync(socket, deadline.Token).ConfigureAwait(false);
                if (frame is null) break;

                switch (HandshakeStep(frame))
                {
                    case "AUTHORIZED":
                        await Send(ChannelRequest()).ConfigureAwait(false);
                        continue;
                    case "CHANNEL_OPENED":
                        await Send(FeedSetup()).ConfigureAwait(false);
                        await Send(HistorySubscription(candleSymbol, from)).ConfigureAwait(false);
                        continue;
                    case { } error when error.StartsWith("ERROR", StringComparison.Ordinal):
                        throw new InvalidOperationException("tastytrade DXLink: " + error);
                }

                var done = false;
                foreach (var e in Decode(frame))
                {
                    if (e.Type != "Candle" || e.Symbol != candleSymbol) continue;
                    if (e.Candle is { } bar && (e.Flags & RemoveEvent) == 0) bars[bar.TimestampUtc] = bar;
                    if ((e.Flags & (SnapshotEnd | SnapshotSnip)) != 0) done = true;
                }

                if (done) break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The feed went quiet before marking the snapshot complete; what arrived is the history.
            Logger.LogDebug("tastytrade history for {Symbol} ended on the timeout with {Count} candles.", candleSymbol, bars.Count);
        }

        try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false); }
        catch { /* closing */ }

        return [.. bars.Values.Where(b => b.TimestampUtc >= from.UtcDateTime - size.ToTimeSpan()).OrderBy(b => b.TimestampUtc)];
    }

    private static async Task<byte[]?> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return ms.ToArray();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await _feed.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// tastytrade's sign-in with a personal OAuth grant: the pasted refresh token and the app's client secret
/// are exchanged at <c>/oauth/token</c> (JSON) for an access token. The refresh token never expires, so it
/// is stored and spent again every fifteen minutes.
/// </summary>
internal sealed class TastytradeSignIn : IBrokerSignIn
{
    private readonly string _restBase;

    public TastytradeSignIn() : this(new TastytradeOptions().RestBaseUrl) { }

    internal TastytradeSignIn(string restBase) => _restBase = restBase;

    public BrokerKind Broker => BrokerKind.Tastytrade;

    public SignInStyle Style => SignInStyle.Token;

    public Task<SessionIssue> SignInAsync(
        HttpClient http, BrokerCredential app, string proof, string redirectUri, DateTimeOffset now, CancellationToken ct)
    {
        var refresh = proof.Trim();
        if (refresh.Length == 0) return Task.FromResult(SessionIssue.Refused("Paste the refresh token from tastytrade's Create Grant page."));
        return OAuthTokens.IssueAsync(() => RefreshAsync(http, _restBase, app, refresh, now, ct));
    }

    public static async Task<KeptSession> RefreshAsync(
        HttpClient http, string restBase, BrokerCredential app, string refreshToken, DateTimeOffset now, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_secret"] = app.Secret.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(app.Key)) body["client_id"] = app.Key.Trim();

        var (status, root, text) = await SignInProof.PostAsync(http, $"{restBase}/oauth/token",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        if (status is < 200 or >= 300)
            throw new InvalidOperationException($"tastytrade refused: {OAuthTokens.Refusal(root) ?? SignInProof.Text(root, "error.message") ?? text} (HTTP {status})");

        return OAuthTokens.Read(root, now, new KeptSession { RefreshToken = refreshToken }, "tastytrade");
    }
}
