using System.Buffers.Binary;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;

namespace TradingTerminal.Infrastructure.AngelOne;

/// <summary>
/// Angel One SmartAPI: quotes, history and SmartStream 2.0.
///
/// <para><b>Symbols are <c>EXCHANGE:TOKEN</c></b> (<c>NSE:2885</c> is Reliance), the token from Angel's scrip
/// master — for NSE equities, the exchange's own token.</para>
///
/// <para><b>SmartStream</b> (<c>wss://smartapisocket.angelone.in/smart-stream</c>) authenticates in the
/// upgrade headers, not the URL, and answers in little-endian binary, one packet per frame. Snap-quote mode
/// carries the five best bids and offers at bytes 147–347 as twenty-byte entries whose first field says
/// the side (0 is a bid). Prices are paise. The client pings with the text <c>ping</c> every ten
/// seconds.</para>
///
/// <para>Written from SmartAPI's official Python client (REST routes, headers, the binary layout),
/// 2026-09-25. Not yet run against a real account.</para>
/// </summary>
internal sealed class RealAngelOneClient : RestBrokerClient<AngelOneOptions>
{
    private readonly SharedFeed<AngelPacket> _feed;

    public RealAngelOneClient(ILogger<RealAngelOneClient> logger, IOptions<AngelOneOptions> options, IBrokerCredentialSource credentials)
        : base(logger, options.Value, credentials)
    {
        _feed = new SharedFeed<AngelPacket>(new FeedProtocol<AngelPacket>
        {
            Name = "Angel One",
            Endpoint = _ =>
            {
                var c = Credential;
                var session = AngelSession.Read(c.Session);
                return Task.FromResult(new FeedEndpoint(new Uri(Options.WsBaseUrl),
                [
                    new("Authorization", session.Jwt),
                    new("x-api-key", c.Key),
                    new("x-client-code", c.Account),
                    new("x-feed-token", session.Feed),
                ]));
            },
            OnConnect = keys => keys.Count == 0 ? [] : [SubscribeMessage(keys)],
            OnAdd = key => [SubscribeMessage([key])],
            Decode = frame => DecodePacket(frame) is { } p ? [new(p.Key, p)] : [],
            Ping = "ping",
            PingSeconds = 10,
            InitialDelaySeconds = Options.ReconnectInitialDelaySeconds,
            MaxDelaySeconds = Options.ReconnectMaxDelaySeconds,
        }, logger);
    }

    public override BrokerKind Kind => BrokerKind.AngelOne;
    protected override string BrokerName => "Angel One";
    protected override string SignInAdvice => "Sign in to Angel One in the login window — SmartAPI sessions last one trading day.";

    /// <summary>Angel's exchange-type numbers, used by SmartStream.</summary>
    internal static int ExchangeType(string exchange) => exchange.ToUpperInvariant() switch
    {
        "NSE" => 1,
        "NFO" => 2,
        "BSE" => 3,
        "BFO" => 4,
        "MCX" => 5,
        "NCX" or "NCDEX" => 7,
        "CDS" => 13,
        _ => 1,
    };

    private static (string Exchange, string Token) Split(string symbol)
    {
        var colon = symbol.IndexOf(':');
        return colon > 0 ? (symbol[..colon].ToUpperInvariant(), symbol[(colon + 1)..]) : ("NSE", symbol);
    }

    /// <summary>The dispatch key: exchange type and token, which is what a SmartStream packet names.</summary>
    private static string Key(string symbol)
    {
        var (exchange, token) = Split(symbol);
        return $"{ExchangeType(exchange)}:{token}";
    }

    private static string SubscribeMessage(IReadOnlyCollection<string> keys)
    {
        var groups = keys.Select(k => k.Split(':')).GroupBy(p => p[0])
            .Select(g => $"{{\"exchangeType\":{g.Key},\"tokens\":[{string.Join(',', g.Select(p => $"\"{p[1]}\""))}]}}");
        // Mode 3 is snap-quote: last price, day stats and the five best bids and offers.
        return $"{{\"correlationID\":\"daxalgo\",\"action\":1,\"params\":{{\"mode\":3,\"tokenList\":[{string.Join(',', groups)}]}}}}";
    }

    private HttpRequestMessage Post(string path, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Options.RestBaseUrl + path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        AngelHeaders.Apply(request, Credential.Key, AngelSession.Read(Credential.Session).Jwt);
        return request;
    }

    protected override async Task CheckSessionAsync(CancellationToken ct)
    {
        var (doc, body) = await SendJsonAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Options.RestBaseUrl + "/rest/secure/angelbroking/user/v1/getProfile");
            AngelHeaders.Apply(request, Credential.Key, AngelSession.Read(Credential.Session).Jwt);
            return request;
        }, ct).ConfigureAwait(false);
        using (doc)
        {
            // SmartAPI answers a bad token with HTTP 200 and a false flag — "success" on an invalid token
            // (seen live, 2026-09-25: {"success":false,"message":"Invalid Token","errorCode":"AG8001"}), "status" elsewhere.
            if (AngelAnswer.IsRefusal(doc.RootElement))
                throw new InvalidOperationException($"Angel One refused the session: {AngelAnswer.Words(doc.RootElement)}");
        }
    }

    // ── History ─────────────────────────────────────────────────────────────────────────────────

    protected override bool HasInterval(BarSize size) => true;

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, TimeSpan span, CancellationToken ct)
    {
        var (exchange, token) = Split(symbol);
        var cap = size switch
        {
            BarSize.OneMinute => TimeSpan.FromDays(30),
            BarSize.ThreeMinutes => TimeSpan.FromDays(60),
            BarSize.FiveMinutes => TimeSpan.FromDays(100),
            BarSize.FifteenMinutes => TimeSpan.FromDays(200),
            BarSize.OneHour => TimeSpan.FromDays(400),
            _ => TimeSpan.FromDays(2000),
        };
        var to = DateTime.UtcNow;
        var from = to - TimeSpan.FromTicks(Math.Min(Math.Max(span.Ticks, TimeSpan.FromDays(1).Ticks), cap.Ticks));
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["exchange"] = exchange,
            ["symboltoken"] = token,
            ["interval"] = Interval(size),
            ["fromdate"] = IndiaTime.Format(from, "yyyy-MM-dd HH:mm"),
            ["todate"] = IndiaTime.Format(to, "yyyy-MM-dd HH:mm"),
        });
        var (doc, body) = await SendJsonAsync(() => Post("/rest/secure/angelbroking/historical/v1/getCandleData", json), ct).ConfigureAwait(false);
        using (doc)
            return WireFormat.OrWarn(ParseCandles(doc.RootElement, Options.SizeScale), body, Logger, BrokerName, "candles");
    }

    /// <summary><c>{"status":true,"data":[["2023-09-06T11:15:00+05:30", o, h, l, c, v], …]}</c>.</summary>
    internal static IReadOnlyList<Bar> ParseCandles(JsonElement root, double scale)
    {
        var bars = new List<Bar>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return bars;
        foreach (var row in data.EnumerateArray())
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
        BarSize.OneMinute => "ONE_MINUTE",
        BarSize.ThreeMinutes => "THREE_MINUTE",
        BarSize.FiveMinutes => "FIVE_MINUTE",
        BarSize.FifteenMinutes => "FIFTEEN_MINUTE",
        BarSize.OneHour => "ONE_HOUR",
        BarSize.OneDay => "ONE_DAY",
        _ => "ONE_MINUTE",
    };

    // ── SmartStream ─────────────────────────────────────────────────────────────────────────────

    public override async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var packet in _feed.SubscribeAsync(Key(Symbol(contract)), ct).ConfigureAwait(false))
            yield return packet.Bids.Count > 0 && packet.Asks.Count > 0
                ? new Tick(packet.TimeUtc, packet.Bids[0].Price, packet.Asks[0].Price, packet.Bids[0].Size, packet.Asks[0].Size)
                : new Tick(packet.TimeUtc, packet.LastPrice, packet.LastPrice, 0, 0);
    }

    public override async IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var packet in _feed.SubscribeAsync(Key(Symbol(contract)), ct).ConfigureAwait(false))
            if (packet.Bids.Count > 0 || packet.Asks.Count > 0)
                yield return new DepthSnapshot(packet.TimeUtc, [.. packet.Bids.Take(levels)], [.. packet.Asks.Take(levels)]);
    }

    internal sealed record AngelPacket(string Key, double LastPrice, DateTime TimeUtc, IReadOnlyList<DepthLevel> Bids, IReadOnlyList<DepthLevel> Asks);

    /// <summary>
    /// One SmartStream packet: mode (1), exchange type (1), token (25, NUL-padded ASCII), sequence (8),
    /// exchange time in ms (8), last price in paise (8), … and, in snap-quote mode, ten depth entries of
    /// 20 bytes at 147: side flag (int16, 0 = bid), quantity (int64), price (int64), orders (int16).
    /// </summary>
    internal static AngelPacket? DecodePacket(byte[] p)
    {
        if (p.Length < 51) return null;   // "pong" and anything else short

        var exchangeType = p[1];
        var token = Encoding.ASCII.GetString(p, 2, 25).TrimEnd('\0');
        var nul = token.IndexOf('\0');
        if (nul >= 0) token = token[..nul];

        var divisor = exchangeType == 13 ? 10_000_000d : 100d;   // CDS carries four more decimals
        long L(int at) => BinaryPrimitives.ReadInt64LittleEndian(p.AsSpan(at, 8));

        // The unit of the exchange time is not documented; it is read from its magnitude.
        var time = Crypto.CryptoConvert.MsUtc(L(35));
        var bids = new List<DepthLevel>(5);
        var asks = new List<DepthLevel>(5);

        if (p.Length >= 347)
        {
            for (var i = 0; i < 10; i++)
            {
                var at = 147 + i * 20;
                var flag = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(at, 2));
                var quantity = L(at + 2);
                var price = L(at + 10) / divisor;
                if (price <= 0 || quantity <= 0) continue;
                (flag == 0 ? bids : asks).Add(new DepthLevel(price, quantity));
            }

            bids.Sort((a, b) => b.Price.CompareTo(a.Price));
            asks.Sort((a, b) => a.Price.CompareTo(b.Price));
        }

        return new AngelPacket($"{exchangeType}:{token}", L(43) / divisor, time, bids, asks);
    }

    public override async ValueTask DisposeAsync()
    {
        await _feed.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>What an Angel One sign-in issues: the REST token and the SmartStream feed token, stored together.</summary>
/// <summary>
/// How SmartAPI says no. It answers with HTTP 200 and a false flag, and the flag's name depends on who refused:
/// an invalid token is <c>{"success":false,"message","errorCode"}</c>, an order or data refusal
/// <c>{"status":false,"message","errorcode"}</c>. Reading only one of them takes the other for success.
/// </summary>
internal static class AngelAnswer
{
    public static bool IsRefusal(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        ((root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.False) ||
         (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False));

    /// <summary>The message and the error code, whichever spelling the code arrived under.</summary>
    public static string? Words(JsonElement root)
    {
        var message = SignInProof.Text(root, "message");
        var code = SignInProof.Text(root, "errorcode") is { Length: > 0 } lower ? lower : SignInProof.Text(root, "errorCode");
        return string.IsNullOrWhiteSpace(message) ? code : string.IsNullOrWhiteSpace(code) ? message : $"{message} ({code})";
    }
}

internal readonly record struct AngelSession(string Jwt, string Feed)
{
    public string Write() => JsonSerializer.Serialize(new Dictionary<string, string> { ["jwt"] = Jwt, ["feed"] = Feed });

    public static AngelSession Read(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return default;
        try
        {
            using var doc = JsonDocument.Parse(stored);
            return new AngelSession(
                SignInProof.Text(doc.RootElement, "jwt") ?? string.Empty,
                SignInProof.Text(doc.RootElement, "feed") ?? string.Empty);
        }
        catch (JsonException)
        {
            return new AngelSession(stored, string.Empty);
        }
    }
}

/// <summary>The headers SmartAPI requires on every call. It asks for the client's IP and MAC addresses;
/// the local ones are sent, as its own client does.</summary>
internal static class AngelHeaders
{
    private static readonly Lazy<(string Ip, string Mac)> Machine = new(() =>
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
            var ip = nic?.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            var mac = nic is null ? null : string.Join(':', nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
            return (ip ?? "127.0.0.1", string.IsNullOrEmpty(mac) ? "00:00:00:00:00:00" : mac);
        }
        catch
        {
            return ("127.0.0.1", "00:00:00:00:00:00");
        }
    });

    public static void Apply(HttpRequestMessage request, string apiKey, string? jwt)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("X-UserType", "USER");
        request.Headers.TryAddWithoutValidation("X-SourceID", "WEB");
        request.Headers.TryAddWithoutValidation("X-ClientLocalIP", Machine.Value.Ip);
        request.Headers.TryAddWithoutValidation("X-ClientPublicIP", Machine.Value.Ip);
        request.Headers.TryAddWithoutValidation("X-MACAddress", Machine.Value.Mac);
        request.Headers.TryAddWithoutValidation("X-PrivateKey", apiKey);
        if (!string.IsNullOrEmpty(jwt)) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {jwt}");
    }
}

/// <summary>
/// SmartAPI sign-in: client code, PIN and the authenticator's six digits, posted to
/// <c>loginByPassword</c>. The code is the one typed, or — if the authenticator's setup key was stored — the
/// one computed now. Stored: key = SmartAPI key, account = client code, secret = PIN, passphrase = setup key.
/// </summary>
internal sealed class AngelOneSignIn : IBrokerSignIn
{
    public BrokerKind Broker => BrokerKind.AngelOne;

    public SignInStyle Style => SignInStyle.Code;

    public async Task<SessionIssue> SignInAsync(
        HttpClient http, BrokerCredential app, string proof, string redirectUri, DateTimeOffset now, CancellationToken ct)
    {
        var totp = Totp.IsCode(proof.Trim()) ? proof.Trim()
            : !string.IsNullOrWhiteSpace(app.Passphrase) ? Totp.Compute(app.Passphrase, now)
            : null;
        if (totp is null) return SessionIssue.Refused("Enter the six-digit code from your authenticator app.");

        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["clientcode"] = app.Account.Trim(),
            ["password"] = app.Secret.Trim(),
            ["totp"] = totp,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://apiconnect.angelone.in/rest/auth/angelbroking/user/v1/loginByPassword")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        AngelHeaders.Apply(request, app.Key.Trim(), jwt: null);
        var (status, root, body) = await SignInProof.SendAsync(http, request, ct).ConfigureAwait(false);

        if (SignInProof.Text(root, "data.jwtToken") is not { Length: > 0 } jwt)
            return SignInProof.Refusal(status, body, SignInProof.Text(root, "message"), SignInProof.Text(root, "errorcode"));

        // The token sometimes arrives with its "Bearer " prefix already on; it is stored bare.
        if (jwt.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) jwt = jwt[7..];
        var session = new AngelSession(jwt, SignInProof.Text(root, "data.feedToken") ?? string.Empty);
        return SessionIssue.Issued(session.Write(), app.Account.Trim());
    }
}
