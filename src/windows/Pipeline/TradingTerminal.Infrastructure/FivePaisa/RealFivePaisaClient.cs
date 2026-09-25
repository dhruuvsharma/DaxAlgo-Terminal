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

namespace TradingTerminal.Infrastructure.FivePaisa;

/// <summary>
/// 5paisa Xstream: history over REST, quotes and depth over its JSON feed.
///
/// <para><b>Symbols are <c>EXCH:TYPE:SCRIPCODE</c></b> — <c>N:C:2885</c> is NSE, cash, Reliance; the scrip
/// code for an NSE equity is the exchange's own token. Exchanges are <c>N</c>/<c>B</c>/<c>M</c>, types
/// <c>C</c> (cash), <c>D</c> (derivatives), <c>U</c> (currency).</para>
///
/// <para><b>Feed</b> (<c>wss://openfeed.5paisa.com/feeds/api/chat?Value1=TOKEN|CLIENTCODE</c>): one socket
/// carries both <c>MarketFeedV3</c> (last trade and the touch: <c>LastRate</c>, <c>BidRate</c>/<c>BidQty</c>,
/// <c>OffRate</c>/<c>OffQty</c>, <c>TickDt</c>) and <c>MarketDepthService</c> (<c>Details</c> of
/// <c>Price</c>/<c>Quantity</c>/<c>NumberOfOrders</c>, side in <c>BbBuySellFlag</c> — 66 is 'B', a bid).
/// Times are .NET-style <c>/Date(ms)/</c>.</para>
///
/// <para>Written from 5paisa's official Python client (routes, payloads) and its WebSocket documentation
/// (fields), 2026-09-25. Not yet run against a real account.</para>
/// </summary>
internal sealed class RealFivePaisaClient : RestBrokerClient<FivePaisaOptions>
{
    private readonly SharedFeed<FivePaisaPacket> _feed;

    public RealFivePaisaClient(ILogger<RealFivePaisaClient> logger, IOptions<FivePaisaOptions> options, IBrokerCredentialSource credentials)
        : base(logger, options.Value, credentials)
    {
        _feed = new SharedFeed<FivePaisaPacket>(new FeedProtocol<FivePaisaPacket>
        {
            Name = "5paisa",
            Endpoint = _ =>
            {
                var c = Credential;
                return Task.FromResult(FeedEndpoint.At($"{Options.WsBaseUrl}?Value1={c.Session}|{c.Account}"));
            },
            OnConnect = keys => keys.Select(Subscribe),
            OnAdd = key => [Subscribe(key)],
            Decode = frame => Decode(frame).Select(p => new KeyValuePair<string, FivePaisaPacket>(p.Key, p)),
            InitialDelaySeconds = Options.ReconnectInitialDelaySeconds,
            MaxDelaySeconds = Options.ReconnectMaxDelaySeconds,
        }, logger);
    }

    public override BrokerKind Kind => BrokerKind.FivePaisa;
    protected override string BrokerName => "5paisa";
    protected override string SignInAdvice => "Sign in to 5paisa in the login window with a fresh authenticator code.";

    private static (string Exch, string Type, string Scrip) Split(string symbol)
    {
        var parts = symbol.Split(':');
        return parts.Length >= 3 ? (parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant(), parts[2]) : ("N", "C", parts[^1]);
    }

    protected override string ExchangeOf(string symbol) => Split(symbol).Exch switch { "B" => "BSE", "M" => "MCX", _ => "NSE" };

    /// <summary>Feed keys: <c>F|N:C:2885</c> for quotes, <c>D|N:C:2885</c> for depth.</summary>
    private string Subscribe(string key)
    {
        var method = key[0] == 'D' ? "MarketDepthService" : "MarketFeedV3";
        var (exch, type, scrip) = Split(key[2..]);
        return $"{{\"Method\":\"{method}\",\"Operation\":\"Subscribe\",\"ClientCode\":\"{Credential.Account}\","
            + $"\"MarketFeedData\":[{{\"Exch\":\"{exch}\",\"ExchType\":\"{type}\",\"ScripCode\":{scrip}}}]}}";
    }

    private HttpRequestMessage Get(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Credential.Session}");
        return request;
    }

    protected override async Task CheckSessionAsync(CancellationToken ct)
    {
        var probe = Options.Instruments.Length > 0 ? Parse(Options.Instruments[0]).Symbol : "N:C:2885";
        await FetchBarsAsync(probe, BarSize.OneDay, TimeSpan.FromDays(5), ct).ConfigureAwait(false);
    }

    // ── History ─────────────────────────────────────────────────────────────────────────────────

    protected override bool HasInterval(BarSize size) => true;

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, TimeSpan span, CancellationToken ct)
    {
        var (exch, type, scrip) = Split(symbol);
        var to = DateTime.UtcNow;
        var from = to - TimeSpan.FromTicks(Math.Max(span.Ticks, TimeSpan.FromDays(size == BarSize.OneDay ? 10 : 2).Ticks));
        var url = $"{Options.RestBaseUrl}/V2/historical/{exch}/{type}/{scrip}/{Interval(size)}"
            + $"?from={IndiaTime.Format(from, "yyyy-MM-dd")}&end={IndiaTime.Format(to.AddDays(1), "yyyy-MM-dd")}";
        var (doc, body) = await SendJsonAsync(() => Get(url), ct).ConfigureAwait(false);
        using (doc)
            return WireFormat.OrWarn(ParseCandles(doc.RootElement, Options.SizeScale), body, Logger, BrokerName, "candles");
    }

    /// <summary><c>{"data":{"candles":[["2021-05-31T09:15:00", o, h, l, c, v], …]}}</c>, times IST.</summary>
    internal static IReadOnlyList<Bar> ParseCandles(JsonElement root, double scale)
    {
        var bars = new List<Bar>();
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("candles", out var candles)
            || candles.ValueKind != JsonValueKind.Array)
            return bars;

        foreach (var row in candles.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6) continue;
            if (IndiaTime.ToUtc(row[0].GetString()) is not { } time) continue;
            bars.Add(new Bar(time, row[1].GetDouble(), row[2].GetDouble(), row[3].GetDouble(), row[4].GetDouble(),
                (long)Math.Round(row[5].GetDouble() * scale)));
        }

        return bars;
    }

    internal static string Interval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1m",
        BarSize.ThreeMinutes => "3m",
        BarSize.FiveMinutes => "5m",
        BarSize.FifteenMinutes => "15m",
        BarSize.OneHour => "60m",
        BarSize.OneDay => "1d",
        _ => "1m",
    };

    // ── Feed ────────────────────────────────────────────────────────────────────────────────────

    public override async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var p in _feed.SubscribeAsync("F|" + Normal(Symbol(contract)), ct).ConfigureAwait(false))
            if (p.Tick is { } tick) yield return tick;
    }

    public override async IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var p in _feed.SubscribeAsync("D|" + Normal(Symbol(contract)), ct).ConfigureAwait(false))
            if (p.Depth is { } depth) yield return depth with { Bids = [.. depth.Bids.Take(levels)], Asks = [.. depth.Asks.Take(levels)] };
    }

    private static string Normal(string symbol)
    {
        var (exch, type, scrip) = Split(symbol);
        return $"{exch}:{type}:{scrip}";
    }

    internal sealed record FivePaisaPacket(string Key, Tick? Tick, DepthSnapshot? Depth);

    /// <summary>A frame is one message or an array of them; quote messages carry <c>LastRate</c>, depth
    /// messages <c>Details</c>.</summary>
    internal static IEnumerable<FivePaisaPacket> Decode(byte[] frame)
    {
        if (frame.Length == 0 || (frame[0] != (byte)'{' && frame[0] != (byte)'[')) yield break;
        using var doc = JsonDocument.Parse(frame);
        var root = doc.RootElement;
        var messages = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];

        foreach (var m in messages)
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            var key = $"{Str(m, "Exch")}:{Str(m, "ExchType")}:{Str(m, "Token")}";

            if (m.TryGetProperty("Details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                var bids = new List<DepthLevel>();
                var asks = new List<DepthLevel>();
                foreach (var level in details.EnumerateArray())
                {
                    var price = Crypto.CryptoConvert.D(level, "Price");
                    var quantity = (long)Crypto.CryptoConvert.D(level, "Quantity");
                    if (price <= 0 || quantity <= 0) continue;
                    // BbBuySellFlag is a character code: 66 'B' for a bid, 83 'S' for an offer.
                    var flag = level.TryGetProperty("BbBuySellFlag", out var f) ? Crypto.CryptoConvert.D(f) : 0;
                    (flag == 66 ? bids : asks).Add(new DepthLevel(price, quantity));
                }

                bids.Sort((a, b) => b.Price.CompareTo(a.Price));
                asks.Sort((a, b) => a.Price.CompareTo(b.Price));
                yield return new FivePaisaPacket("D|" + key, null, new DepthSnapshot(NetDate(m, "Time"), bids, asks));
            }
            else if (m.TryGetProperty("LastRate", out _))
            {
                var bid = Crypto.CryptoConvert.D(m, "BidRate");
                var ask = Crypto.CryptoConvert.D(m, "OffRate");
                var last = Crypto.CryptoConvert.D(m, "LastRate");
                yield return new FivePaisaPacket("F|" + key,
                    new Tick(NetDate(m, "TickDt"),
                        bid > 0 ? bid : last, ask > 0 ? ask : last,
                        (long)Crypto.CryptoConvert.D(m, "BidQty"), (long)Crypto.CryptoConvert.D(m, "OffQty")),
                    null);
            }
        }
    }

    private static string Str(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.GetRawText()) : string.Empty;

    /// <summary><c>"/Date(1640148238196)/"</c> — milliseconds, sometimes with a zone suffix — as UTC.</summary>
    internal static DateTime NetDate(JsonElement m, string name)
    {
        var text = m.TryGetProperty(name, out var v) ? v.GetString() : null;
        if (text is null) return DateTime.UtcNow;
        var open = text.IndexOf('(');
        if (open < 0) return IndiaTime.ToUtc(text) ?? DateTime.UtcNow;
        var digits = new string(text[(open + 1)..].TakeWhile(char.IsAsciiDigit).ToArray());
        return long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? Crypto.CryptoConvert.MsUtc(ms)
            : DateTime.UtcNow;
    }

    public override async ValueTask DisposeAsync()
    {
        await _feed.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 5paisa sign-in: <c>TOTPLogin</c> (client code, the authenticator's six digits, PIN) issues a request
/// token, which <c>GetAccessToken</c> (with the app's encryption key and user id) exchanges for the access
/// token. Stored: key = app user key, secret = encryption key, passphrase = PIN, account = client code,
/// extra = app user id.
/// </summary>
internal sealed class FivePaisaSignIn : IBrokerSignIn
{
    private const string Root = "https://Openapi.5paisa.com/VendorsAPI/Service1.svc";

    public BrokerKind Broker => BrokerKind.FivePaisa;

    public SignInStyle Style => SignInStyle.Code;

    public async Task<SessionIssue> SignInAsync(
        HttpClient http, BrokerCredential app, string proof, string redirectUri, DateTimeOffset now, CancellationToken ct)
    {
        var totp = proof.Trim();
        if (!Totp.IsCode(totp)) return SessionIssue.Refused("Enter the six-digit code from your authenticator app.");

        var login = Payload(app.Key, new Dictionary<string, string>
        {
            ["Email_ID"] = app.Account.Trim(),
            ["TOTP"] = totp,
            ["PIN"] = app.Passphrase.Trim(),
        });
        var (status, root, body) = await SignInProof.PostAsync(http, $"{Root}/TOTPLogin", login, ct).ConfigureAwait(false);
        if (SignInProof.Text(root, "body.RequestToken") is not { Length: > 0 } requestToken)
            return SignInProof.Refusal(status, body, SignInProof.Text(root, "body.Message"), SignInProof.Text(root, "head.statusDescription"));

        var exchange = Payload(app.Key, new Dictionary<string, string>
        {
            ["RequestToken"] = requestToken,
            ["EncryKey"] = app.Secret.Trim(),
            ["UserId"] = app.Extra.Trim(),
        });
        (status, root, body) = await SignInProof.PostAsync(http, $"{Root}/GetAccessToken", exchange, ct).ConfigureAwait(false);

        return SignInProof.Text(root, "body.AccessToken") is { Length: > 0 } token
            ? SessionIssue.Issued(token, SignInProof.Text(root, "body.ClientCode") ?? app.Account.Trim())
            : SignInProof.Refusal(status, body, SignInProof.Text(root, "body.Message"), SignInProof.Text(root, "head.statusDescription"));
    }

    private static StringContent Payload(string userKey, Dictionary<string, string> body) =>
        new(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["head"] = new Dictionary<string, string> { ["Key"] = userKey.Trim() },
            ["body"] = body,
        }), Encoding.UTF8, "application/json");
}
