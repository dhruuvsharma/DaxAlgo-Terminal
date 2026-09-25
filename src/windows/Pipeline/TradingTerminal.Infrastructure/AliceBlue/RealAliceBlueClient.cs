using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;

namespace TradingTerminal.Infrastructure.AliceBlue;

/// <summary>
/// Alice Blue ANT: history over REST, quotes and depth over its Noren feed.
///
/// <para><b>Symbols are <c>EXCHANGE:TOKEN</c></b> (<c>NSE:2885</c>); the feed writes them <c>NSE|2885</c>.</para>
///
/// <para><b>Feed</b> (<c>wss://ws1.aliceblueonline.com/NorenWS/</c>, the Noren protocol several Indian
/// brokers share): the first message authenticates (<c>t:"c"</c>, the session hashed twice with SHA-256);
/// <c>t:"t"</c> subscribes the touchline and <c>t:"d"</c> the five-level depth. Updates (<c>tf</c>, <c>df</c>)
/// carry <b>only the fields that changed</b> — <c>lp</c>, <c>bp1</c>…<c>bp5</c>, <c>sp1</c>…,
/// <c>bq1</c>…, <c>sq1</c>…, <c>ft</c> (feed time, Unix seconds) — so the full state is kept per
/// instrument and each update merged into it.</para>
///
/// <para>Written from Alice Blue's official Python client (routes, session derivation, the feed
/// handshake) and the Noren field names, 2026-09-25. The history response's field names are an assumption
/// pinned in the tests. Not yet run against a real account.</para>
/// </summary>
internal sealed class RealAliceBlueClient : RestBrokerClient<AliceBlueOptions>
{
    private readonly ConcurrentDictionary<string, NorenState> _state = new(StringComparer.Ordinal);
    private readonly SharedFeed<NorenState> _feed;

    public RealAliceBlueClient(ILogger<RealAliceBlueClient> logger, IOptions<AliceBlueOptions> options, IBrokerCredentialSource credentials)
        : base(logger, options.Value, credentials)
    {
        _feed = new SharedFeed<NorenState>(new FeedProtocol<NorenState>
        {
            Name = "Alice Blue",
            Endpoint = _ => Task.FromResult(FeedEndpoint.At(Options.WsBaseUrl)),
            OnConnect = keys => new[] { ConnectMessage() }.Concat(keys.Count == 0 ? [] : Subscribe(keys)),
            OnAdd = key => Subscribe([key]),
            Decode = frame => Merge(_state, frame).Select(s => new KeyValuePair<string, NorenState>(s.Key, s)),
            Ping = "{\"t\":\"h\",\"k\":\"\"}",
            PingSeconds = 30,
            InitialDelaySeconds = Options.ReconnectInitialDelaySeconds,
            MaxDelaySeconds = Options.ReconnectMaxDelaySeconds,
        }, logger);
    }

    public override BrokerKind Kind => BrokerKind.AliceBlue;
    protected override string BrokerName => "Alice Blue";

    private string UserId => Credential.Account.Trim().ToUpperInvariant();

    private string ConnectMessage()
    {
        var user = UserId + "_API";
        return $"{{\"susertoken\":\"{SusrToken(Credential.Session)}\",\"t\":\"c\",\"actid\":\"{user}\",\"uid\":\"{user}\",\"source\":\"API\"}}";
    }

    /// <summary>The feed's token: the session id hashed twice.</summary>
    internal static string SusrToken(string session) => SignInProof.Sha256Hex(SignInProof.Sha256Hex(session));

    private static IEnumerable<string> Subscribe(IReadOnlyCollection<string> keys)
    {
        var list = string.Join('#', keys);
        yield return $"{{\"k\":\"{list}\",\"t\":\"t\"}}";
        yield return $"{{\"k\":\"{list}\",\"t\":\"d\"}}";
    }

    private static (string Exchange, string Token) Split(string symbol)
    {
        var colon = symbol.IndexOf(':');
        return colon > 0 ? (symbol[..colon].ToUpperInvariant(), symbol[(colon + 1)..]) : ("NSE", symbol);
    }

    private static string Key(string symbol)
    {
        var (exchange, token) = Split(symbol);
        return $"{exchange}|{token}";
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? json = null)
    {
        var request = new HttpRequestMessage(method, $"{Options.RestBaseUrl}/{path}");
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {UserId} {Credential.Session}");
        return request;
    }

    protected override async Task CheckSessionAsync(CancellationToken ct)
    {
        var (doc, _) = await SendJsonAsync(() => Request(HttpMethod.Get, "customer/accountDetails"), ct).ConfigureAwait(false);
        using (doc)
            if (SignInProof.Text(doc.RootElement, "stat") is "Not_Ok")
                throw new InvalidOperationException($"Alice Blue refused the session: {SignInProof.Text(doc.RootElement, "emsg")}");
    }

    // ── History ─────────────────────────────────────────────────────────────────────────────────

    protected override bool HasInterval(BarSize size) => size is BarSize.OneMinute or BarSize.OneDay;

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, TimeSpan span, CancellationToken ct)
    {
        var (exchange, token) = Split(symbol);
        var to = DateTimeOffset.UtcNow;
        var from = to - TimeSpan.FromTicks(Math.Max(span.Ticks, TimeSpan.FromDays(size == BarSize.OneDay ? 10 : 2).Ticks));
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["token"] = token,
            ["exchange"] = exchange,
            ["from"] = from.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            ["to"] = to.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            ["resolution"] = size == BarSize.OneDay ? "D" : "1",
        });
        var (doc, body) = await SendJsonAsync(() => Request(HttpMethod.Post, "chart/history", json), ct).ConfigureAwait(false);
        using (doc)
            return WireFormat.OrWarn(ParseCandles(doc.RootElement, Options.SizeScale), body, Logger, BrokerName, "candles");
    }

    /// <summary><c>{"stat":"Ok","result":[{"time":"2022-09-01 09:15:00","open","high","low","close","volume"}, …]}</c>.</summary>
    internal static IReadOnlyList<Bar> ParseCandles(JsonElement root, double scale)
    {
        var bars = new List<Bar>();
        if (!root.TryGetProperty("result", out var rows) || rows.ValueKind != JsonValueKind.Array) return bars;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var time = row.TryGetProperty("time", out var t) ? IndiaTime.ToUtc(t.GetString()) : null;
            if (time is null) continue;
            bars.Add(new Bar(time.Value,
                Crypto.CryptoConvert.D(row, "open"), Crypto.CryptoConvert.D(row, "high"),
                Crypto.CryptoConvert.D(row, "low"), Crypto.CryptoConvert.D(row, "close"),
                (long)Math.Round(Crypto.CryptoConvert.D(row, "volume") * scale)));
        }

        return bars;
    }

    // ── Feed ────────────────────────────────────────────────────────────────────────────────────

    public override async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var s in _feed.SubscribeAsync(Key(Symbol(contract)), ct).ConfigureAwait(false))
            if (s.ToTick() is { } tick) yield return tick;
    }

    public override async IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var s in _feed.SubscribeAsync(Key(Symbol(contract)), ct).ConfigureAwait(false))
            if (s.ToDepth(levels) is { } depth) yield return depth;
    }

    /// <summary>One instrument's feed state, built up from partial updates.</summary>
    internal sealed class NorenState(string key)
    {
        private readonly Dictionary<string, double> _fields = new(StringComparer.Ordinal);

        public string Key { get; } = key;

        public DateTime TimeUtc { get; private set; } = DateTime.UtcNow;

        public void Apply(JsonElement update)
        {
            foreach (var p in update.EnumerateObject())
            {
                if (p.Name is "t" or "e" or "tk" or "ts") continue;
                var value = Crypto.CryptoConvert.D(p.Value);
                if (p.Name == "ft") TimeUtc = Crypto.CryptoConvert.MsUtc((long)value);
                else _fields[p.Name] = value;
            }
        }

        private double Get(string name) => _fields.TryGetValue(name, out var v) ? v : 0;

        public Tick? ToTick()
        {
            var bid = Get("bp1");
            var ask = Get("sp1");
            var last = Get("lp");
            if (bid <= 0 && ask <= 0 && last <= 0) return null;
            return new Tick(TimeUtc, bid > 0 ? bid : last, ask > 0 ? ask : last, (long)Get("bq1"), (long)Get("sq1"));
        }

        public DepthSnapshot? ToDepth(int levels)
        {
            var bids = new List<DepthLevel>();
            var asks = new List<DepthLevel>();
            for (var i = 1; i <= Math.Min(5, levels); i++)
            {
                if (Get($"bp{i}") is var bp and > 0 && Get($"bq{i}") is var bq and > 0) bids.Add(new DepthLevel(bp, (long)bq));
                if (Get($"sp{i}") is var sp and > 0 && Get($"sq{i}") is var sq and > 0) asks.Add(new DepthLevel(sp, (long)sq));
            }

            return bids.Count == 0 && asks.Count == 0 ? null : new DepthSnapshot(TimeUtc, bids, asks);
        }
    }

    /// <summary>Merges a Noren frame into <paramref name="state"/>; yields the instruments it touched.</summary>
    internal static IEnumerable<NorenState> Merge(ConcurrentDictionary<string, NorenState> state, byte[] frame)
    {
        if (frame.Length == 0 || frame[0] != (byte)'{') yield break;
        using var doc = JsonDocument.Parse(frame);
        var m = doc.RootElement;
        var type = m.TryGetProperty("t", out var t) ? t.GetString() : null;
        if (type is not ("tk" or "tf" or "dk" or "df")) yield break;   // "ck" connect ack, heartbeats
        if (!m.TryGetProperty("e", out var e) || !m.TryGetProperty("tk", out var tk)) yield break;

        var key = $"{e.GetString()}|{tk.GetString()}";
        var entry = state.GetOrAdd(key, k => new NorenState(k));
        lock (entry) entry.Apply(m);
        yield return entry;
    }

    public override async ValueTask DisposeAsync()
    {
        await _feed.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Alice Blue sign-in, with no browser and no code: <c>getAPIEncpkey</c> returns an encryption key for the
/// user; <c>getUserSID</c>, given <c>SHA-256(USERID + apiKey + encKey)</c>, returns the session id.
/// Stored: account = user id, secret = API key, session = session id.
/// </summary>
internal sealed class AliceBlueSignIn : IBrokerSignIn
{
    private const string Root = "https://ant.aliceblueonline.com/rest/AliceBlueAPIService/api";

    public BrokerKind Broker => BrokerKind.AliceBlue;

    public SignInStyle Style => SignInStyle.Credentials;

    public async Task<SessionIssue> SignInAsync(
        HttpClient http, BrokerCredential app, string proof, string redirectUri, DateTimeOffset now, CancellationToken ct)
    {
        var user = app.Account.Trim().ToUpperInvariant();
        if (user.Length == 0 || app.Secret.Trim().Length == 0) return SessionIssue.Refused("Enter the user id and API key.");

        var (status, root, body) = await SignInProof.PostAsync(http, $"{Root}/customer/getAPIEncpkey",
            Json(new() { ["userId"] = user }), ct).ConfigureAwait(false);
        if (SignInProof.Text(root, "encKey") is not { Length: > 0 } encKey)
            return SignInProof.Refusal(status, body, SignInProof.Text(root, "emsg"));

        (status, root, body) = await SignInProof.PostAsync(http, $"{Root}/customer/getUserSID",
            Json(new() { ["userId"] = user, ["userData"] = UserData(user, app.Secret.Trim(), encKey) }), ct).ConfigureAwait(false);

        return SignInProof.Text(root, "sessionID") is { Length: > 0 } session
            ? SessionIssue.Issued(session, user)
            : SignInProof.Refusal(status, body, SignInProof.Text(root, "emsg"));
    }

    internal static string UserData(string userId, string apiKey, string encKey) => SignInProof.Sha256Hex(userId + apiKey + encKey);

    private static StringContent Json(Dictionary<string, string> body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
}
