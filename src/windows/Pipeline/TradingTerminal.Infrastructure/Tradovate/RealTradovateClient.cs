using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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

namespace TradingTerminal.Infrastructure.Tradovate;

/// <summary>What the Tradovate market-data socket delivers for one key: a quote or a book.</summary>
internal sealed record TradovatePacket(Tick? Quote, DepthSnapshot? Book);

/// <summary>
/// Tradovate futures: quotes, the book and charts over its market-data WebSocket.
///
/// <para><b>Session.</b> A credentials sign-in — user name, password and the API key pair — returns a REST
/// access token and a separate market-data token, both good for about eighty minutes. The keeper renews by
/// signing in again with the stored credentials. Market data needs Tradovate's API add-on and a CME data
/// subscription on the account.</para>
///
/// <para><b>The socket protocol.</b> Frames are prefixed: <c>o</c> when open, <c>h</c> for a server heartbeat,
/// <c>a[…]</c> for messages, <c>c</c> when closing. Requests are <c>endpoint\nid\n\nbody</c>; the client
/// must send <c>[]</c> every 2.5 seconds. Events identify an instrument by contract id, not name, so each
/// name is resolved over REST first.</para>
///
/// <para><b>Charts</b> answer a request with history and then keep updating the forming bar, so they run on
/// a socket of their own per request: history closes it at the end-of-history marker, live bars keep it.</para>
///
/// <para>Written 2026-09-25 from Tradovate's API documentation and its example client; not yet run against
/// a real account.</para>
/// </summary>
internal sealed class RealTradovateClient : KeptSessionClient<TradovateOptions>
{
    private readonly SharedFeed<TradovatePacket> _feed;
    private readonly ConcurrentDictionary<string, long> _contracts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, double[]> _quotes = [];
    private string _mdToken = string.Empty;
    private volatile bool _authorized;
    private int _authId;
    private int _requestId;

    public RealTradovateClient(
        ILogger<RealTradovateClient> logger, IOptions<TradovateOptions> options, IBrokerCredentialSource credentials, IBrokerSessionStore store)
        : base(logger, options.Value, credentials, store, BrokerKind.Tradovate)
    {
        _feed = new SharedFeed<TradovatePacket>(new FeedProtocol<TradovatePacket>
        {
            Name = "Tradovate market data",
            Endpoint = OpenFeedAsync,
            OnConnect = _ => [],
            OnAdd = key => _authorized ? [Request(Subscribe(key, on: true), key)] : [],
            OnRemove = key => _authorized ? [Request(Subscribe(key, on: false), key)] : [],
            Reply = Answer,
            Decode = Decode,
            Ping = "[]",
            PingSeconds = 2.5,
        }, logger);
    }

    public override BrokerKind Kind => BrokerKind.Tradovate;
    protected override string BrokerName => "Tradovate";
    protected override string CurrencyOf(string symbol) => "USD";
    protected override string SecTypeOf(string symbol) => "FUT";
    protected override string ExchangeOf(string symbol) => "CME";

    private bool IsDemo => Credential.Extra.Trim().Equals("demo", StringComparison.OrdinalIgnoreCase);
    private string RestBase => IsDemo ? Options.DemoRestBaseUrl : Options.RestBaseUrl;
    private string WsBase => IsDemo ? Options.DemoWsBaseUrl : Options.WsBaseUrl;

    protected override Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct) =>
        TradovateSignIn.AccessTokenAsync(Http, RestBase, app, Options.AppId, DateTimeOffset.UtcNow, ct);

    protected override async Task CheckSessionAsync(CancellationToken ct)
    {
        var (doc, _) = await GetAsync($"{RestBase}/auth/me", ct).ConfigureAwait(false);
        doc.Dispose();
    }

    /// <summary>The contract id for a name (<c>ESZ6</c>), from <c>/contract/find</c>; cached.</summary>
    private async Task<long> ContractIdAsync(string name, CancellationToken ct)
    {
        if (_contracts.TryGetValue(name, out var id)) return id;
        var (doc, body) = await GetAsync($"{RestBase}/contract/find?name={Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
        using (doc)
        {
            id = doc.RootElement.TryGetProperty("id", out var e) ? CryptoConvert.L(e) : 0;
            if (id <= 0) throw new InvalidOperationException($"Tradovate knows no contract {name}: {(body.Length > 200 ? body[..200] : body)}");
        }

        _contracts[name] = id;
        return id;
    }

    // ── Protocol ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A request frame: <c>endpoint\nid\n\nbody</c>.</summary>
    internal static string Frame(string endpoint, int id, string body) => $"{endpoint}\n{id}\n\n{body}";

    private string Request((string Endpoint, string Body) request, string key) =>
        Frame(request.Endpoint, Interlocked.Increment(ref _requestId), request.Body);

    /// <summary>The subscribe or unsubscribe for a key <c>q:id</c> (quotes) or <c>d:id</c> (book).</summary>
    internal static (string Endpoint, string Body) Subscribe(string key, bool on)
    {
        var endpoint = (key[0], on) switch
        {
            ('d', true) => "md/subscribeDOM",
            ('d', false) => "md/unsubscribeDOM",
            (_, true) => "md/subscribeQuote",
            _ => "md/unsubscribeQuote",
        };
        return (endpoint, $$"""{"symbol":{{key[2..]}}}""");
    }

    private async Task<FeedEndpoint> OpenFeedAsync(CancellationToken ct)
    {
        _authorized = false;
        var session = await Keeper.CurrentAsync(ct).ConfigureAwait(false);
        _mdToken = session.Extra;
        lock (_quotes) _quotes.Clear();
        return FeedEndpoint.At(WsBase);
    }

    /// <summary>Authorises when the socket says it is open, and subscribes everything once authorised.</summary>
    private IEnumerable<string> Answer(byte[] frame, IReadOnlyCollection<string> keys)
    {
        if (frame.Length > 0 && frame[0] == (byte)'o')
        {
            _authId = Interlocked.Increment(ref _requestId);
            return [Frame("authorize", _authId, _mdToken)];
        }

        foreach (var message in Messages(frame))
        {
            if (!message.TryGetProperty("i", out var i) || CryptoConvert.L(i) != _authId) continue;
            var status = message.TryGetProperty("s", out var s) ? CryptoConvert.L(s) : 0;
            if (status != 200)
            {
                Logger.LogError("Tradovate refused the market-data token (status {Status}): {Detail}", status,
                    message.TryGetProperty("d", out var d) ? d.GetRawText() : "");
                return [];
            }

            _authorized = true;
            return [.. keys.Select(key => Request(Subscribe(key, on: true), key))];
        }

        return [];
    }

    /// <summary>The messages of an <c>a[…]</c> frame; nothing for any other kind.</summary>
    internal static IReadOnlyList<JsonElement> Messages(byte[] frame)
    {
        if (frame.Length < 3 || frame[0] != (byte)'a') return [];
        using var doc = JsonDocument.Parse(frame.AsMemory(1));
        return doc.RootElement.ValueKind == JsonValueKind.Array ? [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())] : [];
    }

    private IEnumerable<KeyValuePair<string, TradovatePacket>> Decode(byte[] frame)
    {
        lock (_quotes) return Decode(frame, _quotes, Options.SizeScale);
    }

    /// <summary>
    /// <c>{"e":"md","d":{"quotes":[{"timestamp","contractId","entries":{"Bid":{"price","size"},"Offer":{…}}}]}}</c>
    /// and <c>{"e":"md","d":{"doms":[{"contractId","timestamp","bids":[{"price","size"}],"offers":[…]}]}}</c>.
    /// Quote entries are merged per contract, since an update need not carry both sides.
    /// </summary>
    internal static IEnumerable<KeyValuePair<string, TradovatePacket>> Decode(byte[] frame, Dictionary<long, double[]> state, double scale)
    {
        var packets = new List<KeyValuePair<string, TradovatePacket>>();
        foreach (var message in Messages(frame))
        {
            if (SignInProof.Text(message, "e") != "md" || !message.TryGetProperty("d", out var d)) continue;

            if (d.TryGetProperty("quotes", out var quotes) && quotes.ValueKind == JsonValueKind.Array)
                foreach (var quote in quotes.EnumerateArray())
                {
                    var id = quote.TryGetProperty("contractId", out var c) ? CryptoConvert.L(c) : 0;
                    if (id <= 0 || !quote.TryGetProperty("entries", out var entries)) continue;
                    if (!state.TryGetValue(id, out var q)) state[id] = q = new double[4];
                    if (entries.TryGetProperty("Bid", out var bid)) { q[0] = CryptoConvert.D(bid, "price"); q[2] = CryptoConvert.D(bid, "size"); }
                    if (entries.TryGetProperty("Offer", out var offer)) { q[1] = CryptoConvert.D(offer, "price"); q[3] = CryptoConvert.D(offer, "size"); }
                    if (q[0] > 0 && q[1] > 0)
                        packets.Add(new("q:" + id.ToString(CultureInfo.InvariantCulture), new TradovatePacket(
                            new Tick(Time(quote), q[0], q[1], (long)Math.Round(q[2] * scale), (long)Math.Round(q[3] * scale)), null)));
                }

            if (d.TryGetProperty("doms", out var doms) && doms.ValueKind == JsonValueKind.Array)
                foreach (var dom in doms.EnumerateArray())
                {
                    var id = dom.TryGetProperty("contractId", out var c) ? CryptoConvert.L(c) : 0;
                    if (id <= 0) continue;
                    packets.Add(new("d:" + id.ToString(CultureInfo.InvariantCulture), new TradovatePacket(null,
                        new DepthSnapshot(Time(dom), Levels(dom, "bids", scale), Levels(dom, "offers", scale)))));
                }
        }

        return packets;
    }

    private static DateTime Time(JsonElement e) =>
        SignInProof.Text(e, "timestamp") is { } t
        && DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : DateTime.UtcNow;

    private static List<DepthLevel> Levels(JsonElement dom, string side, double scale)
    {
        var levels = new List<DepthLevel>();
        if (!dom.TryGetProperty(side, out var rows) || rows.ValueKind != JsonValueKind.Array) return levels;
        foreach (var row in rows.EnumerateArray())
        {
            var (price, size) = (CryptoConvert.D(row, "price"), CryptoConvert.D(row, "size"));
            if (price > 0 && size > 0) levels.Add(new DepthLevel(price, (long)Math.Round(size * scale)));
        }

        return levels;
    }

    // ── Streams ─────────────────────────────────────────────────────────────────────────────────

    public override async IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var id = await ContractIdAsync(Symbol(contract), ct).ConfigureAwait(false);
        await foreach (var packet in _feed.SubscribeAsync("q:" + id.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false))
            if (packet.Quote is { } quote) yield return quote;
    }

    public override async IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var id = await ContractIdAsync(Symbol(contract), ct).ConfigureAwait(false);
        await foreach (var packet in _feed.SubscribeAsync("d:" + id.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false))
            if (packet.Book is { } book) yield return book with { Bids = [.. book.Bids.Take(levels)], Asks = [.. book.Asks.Take(levels)] };
    }

    // ── Charts ──────────────────────────────────────────────────────────────────────────────────

    protected override bool HasInterval(BarSize size) => true;

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, TimeSpan span, CancellationToken ct)
    {
        var count = Math.Clamp((int)Math.Ceiling(span / size.ToTimeSpan()) + 1, 1, 5000);
        var bars = new SortedDictionary<DateTime, Bar>();
        await foreach (var bar in ChartAsync(symbol, size, count, live: false, ct).ConfigureAwait(false))
            bars[bar.TimestampUtc] = bar;
        return [.. bars.Values];
    }

    public override async IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Bar? latest = null;
        await foreach (var bar in ChartAsync(Symbol(contract), barSize, 2, live: true, ct).ConfigureAwait(false))
        {
            if (latest is not null && bar.TimestampUtc < latest.TimestampUtc) continue;
            latest = bar;
            yield return bar;
        }
    }

    /// <summary>The <c>md/getChart</c> body for <paramref name="count"/> bars of <paramref name="size"/>.</summary>
    internal static string ChartRequest(string symbol, BarSize size, int count) => new JsonObject
    {
        ["symbol"] = symbol,
        ["chartDescription"] = new JsonObject
        {
            ["underlyingType"] = size == BarSize.OneDay ? "DailyBar" : "MinuteBar",
            ["elementSize"] = size == BarSize.OneDay ? 1 : (int)size.ToTimeSpan().TotalMinutes,
            ["elementSizeUnit"] = "UnderlyingUnits",
            ["withHistogram"] = false,
        },
        ["timeRange"] = new JsonObject { ["asMuchAsElements"] = count },
    }.ToJsonString();

    /// <summary>
    /// <c>{"e":"chart","d":{"charts":[{"id","td","bars":[{"timestamp","open","high","low","close","upVolume",
    /// "downVolume",…}]},{"id","eoh":true}]}}</c>. Volume is up volume plus down volume.
    /// </summary>
    internal static (List<Bar> Bars, bool EndOfHistory) ParseChart(JsonElement message)
    {
        var bars = new List<Bar>();
        var eoh = false;
        if (SignInProof.Text(message, "e") != "chart" || !message.TryGetProperty("d", out var d)
            || !d.TryGetProperty("charts", out var charts) || charts.ValueKind != JsonValueKind.Array)
            return (bars, eoh);

        foreach (var chart in charts.EnumerateArray())
        {
            if (chart.TryGetProperty("eoh", out var end) && end.ValueKind == JsonValueKind.True) eoh = true;
            if (!chart.TryGetProperty("bars", out var rows) || rows.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in rows.EnumerateArray())
                bars.Add(new Bar(Time(row),
                    CryptoConvert.D(row, "open"), CryptoConvert.D(row, "high"), CryptoConvert.D(row, "low"), CryptoConvert.D(row, "close"),
                    (long)(CryptoConvert.D(row, "upVolume") + CryptoConvert.D(row, "downVolume"))));
        }

        return (bars, eoh);
    }

    private async IAsyncEnumerable<Bar> ChartAsync(string symbol, BarSize size, int count, bool live, [EnumeratorCancellation] CancellationToken ct)
    {
        var session = await Keeper.CurrentAsync(ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!live) cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(WsBase), cts.Token).ConfigureAwait(false);

        var gate = new SemaphoreSlim(1, 1);
        async Task Send(string text)
        {
            await gate.WaitAsync(cts.Token).ConfigureAwait(false);
            try { await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false); }
            finally { gate.Release(); }
        }

        var heartbeat = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                await Task.Delay(TimeSpan.FromSeconds(2.5), cts.Token).ConfigureAwait(false);
                await Send("[]").ConfigureAwait(false);
            }
        }, cts.Token);

        try
        {
            while (true)
            {
                byte[]? frame;
                try
                {
                    frame = await ReceiveAsync(socket, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Logger.LogWarning("Tradovate chart for {Symbol} timed out before the end of its history.", symbol);
                    yield break;
                }

                if (frame is null) yield break;
                if (frame.Length > 0 && frame[0] == (byte)'o')
                {
                    await Send(Frame("authorize", 1, session.Extra)).ConfigureAwait(false);
                    continue;
                }

                foreach (var message in Messages(frame))
                {
                    if (message.TryGetProperty("i", out var i) && CryptoConvert.L(i) == 1)
                    {
                        if (message.TryGetProperty("s", out var s) && CryptoConvert.L(s) != 200)
                            throw new InvalidOperationException($"Tradovate refused the market-data token: {message.GetRawText()}");
                        await Send(Frame("md/getChart", 2, ChartRequest(symbol, size, count))).ConfigureAwait(false);
                        continue;
                    }

                    var (bars, eoh) = ParseChart(message);
                    foreach (var bar in bars) yield return bar;
                    if (eoh && !live) yield break;
                }
            }
        }
        finally
        {
            cts.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch { /* stopped */ }
            try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false); }
            catch { /* closing */ }
            gate.Dispose();
        }
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
/// Tradovate's credentials sign-in: <c>POST /auth/accesstokenrequest</c> with the user name, password and
/// the API key's <c>cid</c> and <c>sec</c>. A penalty ticket (<c>p-ticket</c>, after repeated failures) is
/// waited out and retried once; a captcha cannot be answered here and is refused with that said.
/// Stored: key = cid, secret = sec, account = user name, passphrase = password, extra = <c>live</c>/<c>demo</c>.
/// </summary>
internal sealed class TradovateSignIn : IBrokerSignIn
{
    private readonly TradovateOptions _options;

    public TradovateSignIn() : this(new TradovateOptions()) { }

    internal TradovateSignIn(TradovateOptions options) => _options = options;

    public BrokerKind Broker => BrokerKind.Tradovate;

    public SignInStyle Style => SignInStyle.Credentials;

    public Task<SessionIssue> SignInAsync(
        HttpClient http, BrokerCredential app, string proof, string redirectUri, DateTimeOffset now, CancellationToken ct)
    {
        var rest = app.Extra.Trim().Equals("demo", StringComparison.OrdinalIgnoreCase) ? _options.DemoRestBaseUrl : _options.RestBaseUrl;
        return OAuthTokens.IssueAsync(() => AccessTokenAsync(http, rest, app, _options.AppId, now, ct), app.Account.Trim());
    }

    /// <summary>A stable device id for this machine and user — Tradovate asks for one, and a new id on every
    /// sign-in looks like a new device each time.</summary>
    internal static string DeviceId() =>
        new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"daxalgo|{Environment.MachineName}|{Environment.UserName}"))[..16]).ToString();

    internal static JsonObject Body(BrokerCredential app, string appId, string? ticket = null)
    {
        var body = new JsonObject
        {
            ["name"] = app.Account.Trim(),
            ["password"] = app.Passphrase,
            ["appId"] = appId,
            ["appVersion"] = "1.0",
            ["cid"] = app.Key.Trim(),
            ["sec"] = app.Secret.Trim(),
            ["deviceId"] = DeviceId(),
        };
        if (ticket is not null) body["p-ticket"] = ticket;
        return body;
    }

    public static async Task<KeptSession> AccessTokenAsync(
        HttpClient http, string restBase, BrokerCredential app, string appId, DateTimeOffset now, CancellationToken ct)
    {
        string? ticket = null;
        for (var attempt = 1; ; attempt++)
        {
            var (status, root, body) = await SignInProof.PostAsync(http, $"{restBase}/auth/accesstokenrequest",
                new StringContent(Body(app, appId, ticket).ToJsonString(), Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);

            if (SignInProof.Text(root, "p-ticket") is { } penalty && attempt == 1)
            {
                if (SignInProof.Text(root, "p-captcha") == "true")
                    throw new InvalidOperationException("Tradovate wants a captcha after failed sign-ins — sign in once on Tradovate's website, then try again.");
                var wait = double.TryParse(SignInProof.Text(root, "p-time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 5;
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 1, 60)), ct).ConfigureAwait(false);
                ticket = penalty;
                continue;
            }

            return Read(status, root, body, restBase);
        }
    }

    /// <summary><c>{"accessToken","mdAccessToken","expirationTime","userId","name"}</c>, or <c>{"errorText"}</c>.</summary>
    internal static KeptSession Read(int status, JsonElement root, string body, string restBase)
    {
        var access = SignInProof.Text(root, "accessToken");
        var md = SignInProof.Text(root, "mdAccessToken");
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(md))
            throw new InvalidOperationException(
                $"Tradovate refused: {SignInProof.Text(root, "errorText") ?? SignInProof.Snippet(body)} (HTTP {status})");

        var expires = DateTimeOffset.TryParse(SignInProof.Text(root, "expirationTime"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var e) ? e : DateTimeOffset.UtcNow.AddMinutes(75);
        return new KeptSession { AccessToken = access, Extra = md, ExpiresUtc = expires, Server = restBase };
    }
}
