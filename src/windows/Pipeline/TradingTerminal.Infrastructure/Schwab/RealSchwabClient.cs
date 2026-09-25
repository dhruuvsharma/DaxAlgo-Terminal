using System.Net.Http;
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

namespace TradingTerminal.Infrastructure.Schwab;

/// <summary>What the Schwab streamer delivers for one key: a quote or a book.</summary>
internal sealed record SchwabPacket(Tick? Quote, DepthSnapshot? Book);

/// <summary>
/// Charles Schwab's Trader API: bars over REST, quotes and the book over the streamer.
///
/// <para><b>Session.</b> OAuth 2: a browser sign-in returns a code, exchanged for a 30-minute access token
/// and a 7-day refresh token. The keeper renews the access token; after seven days Schwab requires the
/// browser sign-in again, and nothing can renew around that.</para>
///
/// <para><b>Streamer.</b> Its address and the ids a LOGIN needs come from <c>/trader/v1/userPreference</c>,
/// fetched on every connection. <c>LEVELONE_EQUITIES</c> sends only the fields that changed, so quotes are
/// merged per symbol; the book services send the whole book each time. Subscriptions wait for the LOGIN to
/// be acknowledged.</para>
///
/// <para>Paths, parameters and field numbers are from Schwab's developer portal and the schwab-py client.
/// Whether quote sizes are shares or round lots could not be confirmed and is pinned in the tests as
/// shares. Written 2026-09-25; not yet run against a real account.</para>
/// </summary>
internal sealed class RealSchwabClient : KeptSessionClient<SchwabOptions>
{
    /// <summary>Streamer field numbers for <c>LEVELONE_EQUITIES</c>.</summary>
    internal const int Bid = 1, Ask = 2, Last = 3, BidSize = 4, AskSize = 5, QuoteTime = 34;

    private readonly SharedFeed<SchwabPacket> _feed;
    private readonly Dictionary<string, double[]> _quotes = new(StringComparer.Ordinal);
    private StreamerInfo? _streamer;
    private string _loginToken = string.Empty;
    private volatile bool _loggedIn;
    private int _requestId;

    internal sealed record StreamerInfo(string Url, string CustomerId, string CorrelId, string Channel, string FunctionId);

    public RealSchwabClient(
        ILogger<RealSchwabClient> logger, IOptions<SchwabOptions> options, IBrokerCredentialSource credentials, IBrokerSessionStore store)
        : base(logger, options.Value, credentials, store, BrokerKind.CharlesSchwab)
    {
        _feed = new SharedFeed<SchwabPacket>(new FeedProtocol<SchwabPacket>
        {
            Name = "Schwab streamer",
            Endpoint = OpenStreamerAsync,
            OnConnect = _ => [Login()],
            OnAdd = key => _loggedIn ? [Subscribe([key], "ADD")] : [],
            OnRemove = key => _loggedIn ? [Subscribe([key], "UNSUBS")] : [],
            Reply = (frame, keys) => AnswerLogin(frame, keys),
            Decode = Decode,
        }, logger);
    }

    public override BrokerKind Kind => BrokerKind.CharlesSchwab;
    protected override string BrokerName => "Schwab";
    protected override string SignInAdvice => "Sign in to Schwab in the login window — a Schwab sign-in lasts seven days.";
    protected override string CurrencyOf(string symbol) => "USD";
    protected override string SecTypeOf(string symbol) => symbol.StartsWith('$') ? "IND" : "STK";
    protected override string ExchangeOf(string symbol) => "SMART";

    protected override Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct) =>
        SchwabSignIn.TokenAsync(Http, Options.AuthBaseUrl, app,
            [new("grant_type", "refresh_token"), new("refresh_token", session.RefreshToken)], DateTimeOffset.UtcNow, session, ct);

    protected override async Task CheckSessionAsync(CancellationToken ct)
    {
        var (doc, _) = await GetAsync($"{Options.RestBaseUrl}/marketdata/v1/quotes?symbols=SPY&fields=quote", ct).ConfigureAwait(false);
        doc.Dispose();
    }

    // ── History ─────────────────────────────────────────────────────────────────────────────────

    protected override bool HasInterval(BarSize size) => size is BarSize.OneMinute or BarSize.FiveMinutes or BarSize.FifteenMinutes or BarSize.OneDay;

    /// <summary>No 3-minute or hourly candles: 3m rolls up from 1m, 1h from 30m.</summary>
    protected override BarSize RollupBase(BarSize size) => size == BarSize.OneHour ? BarSize.FifteenMinutes : BarSize.OneMinute;

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, TimeSpan span, CancellationToken ct)
    {
        var to = DateTimeOffset.UtcNow;
        var from = to - TimeSpan.FromTicks(Math.Max(span.Ticks, TimeSpan.FromDays(size == BarSize.OneDay ? 30 : 2).Ticks));
        var url = $"{Options.RestBaseUrl}/marketdata/v1/pricehistory?symbol={Uri.EscapeDataString(symbol)}&{Frequency(size)}"
            + $"&startDate={from.ToUnixTimeMilliseconds()}&endDate={to.ToUnixTimeMilliseconds()}&needExtendedHoursData=true";
        var (doc, body) = await GetAsync(url, ct).ConfigureAwait(false);
        using (doc) return WireFormat.OrWarn(ParseCandles(doc.RootElement), body, Logger, BrokerName, "candles");
    }

    internal static string Frequency(BarSize size) => size switch
    {
        BarSize.OneDay => "periodType=year&frequencyType=daily&frequency=1",
        BarSize.FiveMinutes => "periodType=day&frequencyType=minute&frequency=5",
        BarSize.FifteenMinutes => "periodType=day&frequencyType=minute&frequency=15",
        _ => "periodType=day&frequencyType=minute&frequency=1",
    };

    /// <summary><c>{"candles":[{"open","high","low","close","volume","datetime":ms}], "symbol", "empty"}</c>.</summary>
    internal static IReadOnlyList<Bar> ParseCandles(JsonElement root)
    {
        var bars = new List<Bar>();
        if (!root.TryGetProperty("candles", out var candles) || candles.ValueKind != JsonValueKind.Array) return bars;
        foreach (var c in candles.EnumerateArray())
            bars.Add(new Bar(CryptoConvert.MsUtc(c, "datetime"),
                CryptoConvert.D(c, "open"), CryptoConvert.D(c, "high"), CryptoConvert.D(c, "low"), CryptoConvert.D(c, "close"),
                (long)CryptoConvert.D(c, "volume")));
        return bars;
    }

    // ── Streamer ────────────────────────────────────────────────────────────────────────────────

    public override async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var packet in _feed.SubscribeAsync("Q:" + Symbol(contract), ct).ConfigureAwait(false))
            if (packet.Quote is { } quote) yield return quote;
    }

    public override async IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var packet in _feed.SubscribeAsync("B:" + Symbol(contract), ct).ConfigureAwait(false))
            if (packet.Book is { } book) yield return book with { Bids = [.. book.Bids.Take(levels)], Asks = [.. book.Asks.Take(levels)] };
    }

    private async Task<FeedEndpoint> OpenStreamerAsync(CancellationToken ct)
    {
        _loggedIn = false;
        var session = await Keeper.CurrentAsync(ct).ConfigureAwait(false);
        var (doc, body) = await GetAsync($"{Options.RestBaseUrl}/trader/v1/userPreference", ct).ConfigureAwait(false);
        using (doc)
        {
            _streamer = ParseStreamerInfo(doc.RootElement)
                ?? throw new InvalidOperationException($"Schwab gave no streamer address: {(body.Length > 200 ? body[..200] : body)}");
        }

        _loginToken = session.AccessToken;
        lock (_quotes) _quotes.Clear();
        return FeedEndpoint.At(_streamer.Url);
    }

    /// <summary><c>{"streamerInfo":[{"streamerSocketUrl","schwabClientCustomerId","schwabClientCorrelId",
    /// "schwabClientChannel","schwabClientFunctionId"}]}</c> — an array of one.</summary>
    internal static StreamerInfo? ParseStreamerInfo(JsonElement root)
    {
        if (!root.TryGetProperty("streamerInfo", out var info)) return null;
        if (info.ValueKind == JsonValueKind.Array)
        {
            if (info.GetArrayLength() == 0) return null;
            info = info[0];
        }

        var url = SignInProof.Text(info, "streamerSocketUrl");
        return string.IsNullOrWhiteSpace(url) ? null : new StreamerInfo(url,
            SignInProof.Text(info, "schwabClientCustomerId") ?? string.Empty,
            SignInProof.Text(info, "schwabClientCorrelId") ?? string.Empty,
            SignInProof.Text(info, "schwabClientChannel") ?? string.Empty,
            SignInProof.Text(info, "schwabClientFunctionId") ?? string.Empty);
    }

    private string Login() => LoginMessage(_streamer!, _loginToken, Interlocked.Increment(ref _requestId));

    internal static string LoginMessage(StreamerInfo s, string accessToken, int id) => Envelope(new JsonObject
    {
        ["requestid"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["service"] = "ADMIN",
        ["command"] = "LOGIN",
        ["SchwabClientCustomerId"] = s.CustomerId,
        ["SchwabClientCorrelId"] = s.CorrelId,
        ["parameters"] = new JsonObject
        {
            ["Authorization"] = accessToken,
            ["SchwabClientChannel"] = s.Channel,
            ["SchwabClientFunctionId"] = s.FunctionId,
        },
    });

    private string Subscribe(IEnumerable<string> keys, string command) =>
        SubscribeMessage(_streamer!, keys, command, Options.BookService, () => Interlocked.Increment(ref _requestId));

    /// <summary>One message carrying a request per service: quotes (<c>Q:</c> keys) and books (<c>B:</c>).</summary>
    internal static string SubscribeMessage(StreamerInfo s, IEnumerable<string> keys, string command, string bookService, Func<int> nextId)
    {
        var requests = new JsonArray();
        foreach (var group in keys.GroupBy(k => k[..2]))
        {
            var (service, fields) = group.Key == "B:" ? (bookService, "0,1,2,3") : ("LEVELONE_EQUITIES", "0,1,2,3,4,5,8,34");
            var parameters = new JsonObject { ["keys"] = string.Join(",", group.Select(k => k[2..])) };
            if (command != "UNSUBS") parameters["fields"] = fields;
            requests.Add(new JsonObject
            {
                ["requestid"] = nextId().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["service"] = service,
                ["command"] = command,
                ["SchwabClientCustomerId"] = s.CustomerId,
                ["SchwabClientCorrelId"] = s.CorrelId,
                ["parameters"] = parameters,
            });
        }

        return new JsonObject { ["requests"] = requests }.ToJsonString();
    }

    private static string Envelope(JsonObject request) => new JsonObject { ["requests"] = new JsonArray(request) }.ToJsonString();

    /// <summary>Subscribes everything wanted once the LOGIN is acknowledged with code 0.</summary>
    private IEnumerable<string> AnswerLogin(byte[] frame, IReadOnlyCollection<string> keys)
    {
        if (LoginResult(frame) is not { } code) return [];
        if (code != 0)
        {
            Logger.LogError("Schwab's streamer refused the login (code {Code}). {Advice}", code, SignInAdvice);
            return [];
        }

        _loggedIn = true;
        return keys.Count == 0 ? [] : [Subscribe(keys, "ADD")];
    }

    /// <summary>The LOGIN response's code, when <paramref name="frame"/> is one.</summary>
    internal static int? LoginResult(byte[] frame)
    {
        // Every frame passes through here; only a LOGIN response is worth parsing.
        if (frame.AsSpan().IndexOf("\"LOGIN\""u8) < 0) return null;
        using var doc = JsonDocument.Parse(frame);
        if (!doc.RootElement.TryGetProperty("response", out var responses) || responses.ValueKind != JsonValueKind.Array) return null;
        foreach (var r in responses.EnumerateArray())
            if (SignInProof.Text(r, "command") == "LOGIN" && r.TryGetProperty("content", out var content))
                return (int)CryptoConvert.L(content.GetProperty("code"));
        return null;
    }

    private IEnumerable<KeyValuePair<string, SchwabPacket>> Decode(byte[] frame)
    {
        lock (_quotes) return Decode(frame, _quotes, Options.SizeScale);
    }

    /// <summary>
    /// <c>{"data":[{"service","timestamp","content":[{"key":"AAPL","1":bid,"2":ask,…}]}]}</c>. Quote fields
    /// arrive only when they change, so each is merged into <paramref name="state"/> before a tick is made.
    /// A book is <c>{"key","1":time,"2":[bid levels],"3":[ask levels]}</c> with each level
    /// <c>{"0":price,"1":size,…}</c>.
    /// </summary>
    internal static IEnumerable<KeyValuePair<string, SchwabPacket>> Decode(byte[] frame, Dictionary<string, double[]> state, double scale)
    {
        var packets = new List<KeyValuePair<string, SchwabPacket>>();
        using var doc = JsonDocument.Parse(frame);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return packets;

        foreach (var item in data.EnumerateArray())
        {
            var service = SignInProof.Text(item, "service") ?? string.Empty;
            var stamp = CryptoConvert.MsUtc(item, "timestamp");
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;

            foreach (var entry in content.EnumerateArray())
            {
                var key = SignInProof.Text(entry, "key");
                if (string.IsNullOrEmpty(key)) continue;

                if (service == "LEVELONE_EQUITIES")
                {
                    if (!state.TryGetValue(key, out var q)) state[key] = q = new double[QuoteTime + 1];
                    foreach (var field in (int[])[Bid, Ask, Last, BidSize, AskSize, QuoteTime])
                        if (entry.TryGetProperty(field.ToString(System.Globalization.CultureInfo.InvariantCulture), out var v))
                            q[field] = CryptoConvert.D(v);
                    if (q[Bid] > 0 && q[Ask] > 0)
                        packets.Add(new("Q:" + key, new SchwabPacket(new Tick(
                            q[QuoteTime] > 0 ? CryptoConvert.MsUtc((long)q[QuoteTime]) : stamp,
                            q[Bid], q[Ask], (long)Math.Round(q[BidSize] * scale), (long)Math.Round(q[AskSize] * scale)), null)));
                }
                else if (service.EndsWith("_BOOK", StringComparison.Ordinal))
                {
                    var time = entry.TryGetProperty("1", out var t) ? CryptoConvert.MsUtc(CryptoConvert.L(t)) : stamp;
                    var bids = Levels(entry, "2", scale);
                    var asks = Levels(entry, "3", scale);
                    if (bids.Count > 0 || asks.Count > 0)
                        packets.Add(new("B:" + key, new SchwabPacket(null, new DepthSnapshot(time, bids, asks))));
                }
            }
        }

        return packets;
    }

    private static List<DepthLevel> Levels(JsonElement entry, string side, double scale)
    {
        var levels = new List<DepthLevel>();
        if (!entry.TryGetProperty(side, out var rows) || rows.ValueKind != JsonValueKind.Array) return levels;
        foreach (var row in rows.EnumerateArray())
        {
            var price = CryptoConvert.D(row, "0");
            var size = CryptoConvert.D(row, "1");
            if (price > 0 && size > 0) levels.Add(new DepthLevel(price, (long)Math.Round(size * scale)));
        }

        return levels;
    }

    public override async ValueTask DisposeAsync()
    {
        await _feed.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Schwab's OAuth 2 sign-in: the authorize page redirects back with <c>?code=…&amp;session=…</c>, and the code
/// is exchanged at <c>/v1/oauth/token</c> under HTTP Basic auth of <c>app key:app secret</c>. The code is
/// URL-encoded in the redirect and ends <c>%40</c>; it is sent decoded.
/// </summary>
internal sealed class SchwabSignIn : IBrokerSignIn
{
    private readonly string _authBase;

    public SchwabSignIn() : this(new SchwabOptions().AuthBaseUrl) { }

    internal SchwabSignIn(string authBase) => _authBase = authBase;

    public BrokerKind Broker => BrokerKind.CharlesSchwab;

    public SignInStyle Style => SignInStyle.Browser;

    public string? SignInUrl(BrokerCredential app, string redirectUri) =>
        $"{_authBase}/authorize?client_id={Uri.EscapeDataString(app.Key.Trim())}&redirect_uri={Uri.EscapeDataString(redirectUri)}";

    public Task<SessionIssue> SignInAsync(
        HttpClient http, BrokerCredential app, string proof, string redirectUri, DateTimeOffset now, CancellationToken ct)
    {
        var code = SignInProof.Parameter(proof, "code");
        if (string.IsNullOrWhiteSpace(code)) return Task.FromResult(SessionIssue.Refused("Paste the address Schwab redirected to — it carries the code."));

        return OAuthTokens.IssueAsync(() => TokenAsync(http, _authBase, app,
            [new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", redirectUri)], now, null, ct));
    }

    public static Task<KeptSession> TokenAsync(
        HttpClient http, string authBase, BrokerCredential app, KeyValuePair<string, string>[] form, DateTimeOffset now,
        KeptSession? previous, CancellationToken ct) =>
        OAuthTokens.PostFormAsync(http, $"{authBase}/token", form, now, previous, "Schwab", ct,
            ("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{app.Key.Trim()}:{app.Secret.Trim()}"))));
}
