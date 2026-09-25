using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;

namespace TradingTerminal.Infrastructure.Zerodha;

/// <summary>
/// Zerodha Kite Connect v3: quotes, history and the binary ticker.
///
/// <para><b>Symbols are <c>EXCHANGE:TRADINGSYMBOL</c></b> (<c>NSE:INFY</c>). History and the ticker want
/// Kite's numeric <c>instrument_token</c> instead; it is resolved on first use from <c>/quote/ltp</c>, which
/// returns it, and cached — so no instrument dump has to be downloaded.</para>
///
/// <para><b>Ticker</b> (<c>wss://ws.kite.trade</c>): one socket for every subscription (Kite allows three per
/// key), <c>full</c> mode. Frames are big-endian: a packet count, then length-prefixed packets. Prices are
/// integers in paise — divided by 100, or by 10⁷ for currency and 10⁴ for BSE currency. A one-byte frame
/// is a heartbeat; a text frame is an order postback or an error.</para>
///
/// <para>Written from Kite Connect's published documentation and its official Python client (byte order),
/// 2026-09-25. Not yet run against a real account.</para>
/// </summary>
internal sealed class RealZerodhaClient : RestBrokerClient<ZerodhaOptions>
{
    private readonly ConcurrentDictionary<string, uint> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly SharedFeed<KitePacket> _feed;

    public RealZerodhaClient(ILogger<RealZerodhaClient> logger, IOptions<ZerodhaOptions> options, IBrokerCredentialSource credentials)
        : base(logger, options.Value, credentials)
    {
        _feed = new SharedFeed<KitePacket>(new FeedProtocol<KitePacket>
        {
            Name = "Zerodha",
            Endpoint = _ =>
            {
                var c = Credential;
                return Task.FromResult(FeedEndpoint.At(
                    $"{Options.WsBaseUrl}?api_key={Uri.EscapeDataString(c.Key)}&access_token={Uri.EscapeDataString(c.Session)}"));
            },
            OnConnect = keys => keys.Count == 0 ? [] : Subscribe(keys),
            OnAdd = key => Subscribe([key]),
            Decode = frame => DecodeFrame(frame).Select(p => new KeyValuePair<string, KitePacket>(p.Token.ToString(System.Globalization.CultureInfo.InvariantCulture), p)),
            InitialDelaySeconds = Options.ReconnectInitialDelaySeconds,
            MaxDelaySeconds = Options.ReconnectMaxDelaySeconds,
        }, logger);
    }

    public override BrokerKind Kind => BrokerKind.Zerodha;
    protected override string BrokerName => "Zerodha";
    protected override string SignInAdvice => "Sign in to Zerodha in the login window — Kite sessions expire at 6 AM each day.";

    protected override string ExchangeOf(string symbol) => symbol.Contains(':') ? symbol[..symbol.IndexOf(':')] : "NSE";

    private static IEnumerable<string> Subscribe(IReadOnlyCollection<string> tokens)
    {
        var list = string.Join(',', tokens);
        yield return $"{{\"a\":\"subscribe\",\"v\":[{list}]}}";
        yield return $"{{\"a\":\"mode\",\"v\":[\"full\",[{list}]]}}";
    }

    private HttpRequestMessage Get(string pathAndQuery)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Options.RestBaseUrl + pathAndQuery);
        var c = Credential;
        request.Headers.TryAddWithoutValidation("X-Kite-Version", "3");
        request.Headers.TryAddWithoutValidation("Authorization", $"token {c.Key}:{c.Session}");
        return request;
    }

    protected override async Task CheckSessionAsync(CancellationToken ct)
    {
        var (doc, _) = await SendJsonAsync(() => Get("/user/profile"), ct).ConfigureAwait(false);
        doc.Dispose();
    }

    /// <summary>Kite's instrument token for <paramref name="symbol"/>, from <c>/quote/ltp</c>.</summary>
    private async Task<uint> TokenAsync(string symbol, CancellationToken ct)
    {
        if (_tokens.TryGetValue(symbol, out var cached)) return cached;
        var (doc, body) = await SendJsonAsync(() => Get($"/quote/ltp?i={Uri.EscapeDataString(symbol)}"), ct).ConfigureAwait(false);
        using (doc)
        {
            var token = ParseToken(doc.RootElement, symbol)
                ?? throw new InvalidOperationException($"Zerodha does not know {symbol}: {(body.Length > 200 ? body[..200] : body)}");
            return _tokens[symbol] = token;
        }
    }

    internal static uint? ParseToken(JsonElement root, string symbol) =>
        root.TryGetProperty("data", out var data) && data.TryGetProperty(symbol, out var entry)
        && entry.TryGetProperty("instrument_token", out var token) && token.TryGetUInt32(out var value)
            ? value
            : null;

    // ── History ─────────────────────────────────────────────────────────────────────────────────

    protected override bool HasInterval(BarSize size) => true;

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, TimeSpan span, CancellationToken ct)
    {
        var token = await TokenAsync(symbol, ct).ConfigureAwait(false);
        var to = DateTime.UtcNow;
        // Kite caps a request's range by interval (60 days of minutes … 2000 of days).
        var cap = size switch
        {
            BarSize.OneMinute => TimeSpan.FromDays(60),
            BarSize.OneHour => TimeSpan.FromDays(400),
            BarSize.OneDay => TimeSpan.FromDays(2000),
            _ => TimeSpan.FromDays(100),
        };
        var from = to - TimeSpan.FromTicks(Math.Min(Math.Max(span.Ticks, TimeSpan.FromDays(1).Ticks), cap.Ticks));
        var query = $"/instruments/historical/{token}/{Interval(size)}?from={Uri.EscapeDataString(IndiaTime.Format(from, "yyyy-MM-dd HH:mm:ss"))}"
            + $"&to={Uri.EscapeDataString(IndiaTime.Format(to, "yyyy-MM-dd HH:mm:ss"))}";
        var (doc, body) = await SendJsonAsync(() => Get(query), ct).ConfigureAwait(false);
        using (doc)
            return WireFormat.OrWarn(ParseCandles(doc.RootElement, Options.SizeScale), body, Logger, BrokerName, "candles");
    }

    /// <summary><c>{"data":{"candles":[["2017-12-15T09:15:00+0530", o, h, l, c, v], …]}}</c>.</summary>
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
        BarSize.OneMinute => "minute",
        BarSize.ThreeMinutes => "3minute",
        BarSize.FiveMinutes => "5minute",
        BarSize.FifteenMinutes => "15minute",
        BarSize.OneHour => "60minute",
        BarSize.OneDay => "day",
        _ => "minute",
    };

    // ── Ticker ──────────────────────────────────────────────────────────────────────────────────

    public override async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var token = await TokenAsync(Symbol(contract), ct).ConfigureAwait(false);
        await foreach (var packet in _feed.SubscribeAsync(token.ToString(System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false))
            yield return packet.ToTick(Options.SizeScale);
    }

    public override async IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var token = await TokenAsync(Symbol(contract), ct).ConfigureAwait(false);
        await foreach (var packet in _feed.SubscribeAsync(token.ToString(System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false))
            if (packet.Bids.Count > 0 || packet.Asks.Count > 0)
                yield return packet.ToDepth(levels, Options.SizeScale);
    }

    /// <summary>One decoded ticker packet. Depth is empty in quote mode and for indices.</summary>
    internal sealed record KitePacket(
        uint Token, double LastPrice, long LastQuantity, long Volume, DateTime TimeUtc,
        IReadOnlyList<DepthLevel> Bids, IReadOnlyList<DepthLevel> Asks)
    {
        public Tick ToTick(double scale) =>
            Bids.Count > 0 && Asks.Count > 0
                ? new Tick(TimeUtc, Bids[0].Price, Asks[0].Price, Bids[0].Size, Asks[0].Size)
                // An index has no book: its "quote" is the level itself.
                : new Tick(TimeUtc, LastPrice, LastPrice, 0, 0);

        public DepthSnapshot ToDepth(int levels, double scale) =>
            new(TimeUtc, [.. Bids.Take(levels)], [.. Asks.Take(levels)]);
    }

    /// <summary>
    /// Decodes a ticker frame: <c>[int16 count][int16 length, packet]…</c>, big-endian. Heartbeats (one byte)
    /// and text frames yield nothing.
    /// </summary>
    internal static IEnumerable<KitePacket> DecodeFrame(byte[] frame)
    {
        if (frame.Length < 4 || frame[0] == (byte)'{') yield break;

        var count = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(0, 2));
        var offset = 2;
        for (var i = 0; i < count && offset + 2 <= frame.Length; i++)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(offset, 2));
            offset += 2;
            if (offset + length > frame.Length) yield break;
            if (DecodePacket(frame.AsSpan(offset, length).ToArray()) is { } packet) yield return packet;
            offset += length;
        }
    }

    /// <summary>One packet: 8 bytes (ltp), 28/32 (index quote/full), 44 (quote) or 184 (full).</summary>
    internal static KitePacket? DecodePacket(byte[] p)
    {
        if (p.Length < 8) return null;
        int I(int at) => BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(at, 4));

        var token = (uint)I(0);
        var divisor = (token & 0xFF) switch
        {
            3 => 10_000_000d,   // CDS — currency, four decimals of paise
            6 => 10_000d,       // BCD — BSE currency
            _ => 100d,
        };
        double Price(int at) => I(at) / divisor;

        if (p.Length == 8)
            return new KitePacket(token, Price(4), 0, 0, DateTime.UtcNow, [], []);

        if (p.Length is 28 or 32)
        {
            // Index: token, ltp, high, low, open, close, change[, exchange timestamp].
            var time = p.Length == 32 ? DateTimeOffset.FromUnixTimeSeconds(I(28)).UtcDateTime : DateTime.UtcNow;
            return new KitePacket(token, Price(4), 0, 0, time, [], []);
        }

        if (p.Length < 44) return null;

        var lastQuantity = I(8);
        var volume = I(16);
        if (p.Length < 184)
            return new KitePacket(token, Price(4), lastQuantity, volume, DateTime.UtcNow, [], []);

        var exchangeTime = I(60);
        var bids = new List<DepthLevel>(5);
        var asks = new List<DepthLevel>(5);
        for (var level = 0; level < 10; level++)
        {
            // Each entry: int32 quantity, int32 price, int16 orders, 2 bytes padding.
            var at = 64 + level * 12;
            var quantity = I(at);
            var price = Price(at + 4);
            if (price <= 0 || quantity <= 0) continue;
            (level < 5 ? bids : asks).Add(new DepthLevel(price, quantity));
        }

        return new KitePacket(token, Price(4), lastQuantity, volume,
            exchangeTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(exchangeTime).UtcDateTime : DateTime.UtcNow, bids, asks);
    }

    public override async ValueTask DisposeAsync()
    {
        await _feed.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Kite Connect sign-in: <c>kite.zerodha.com/connect/login?v=3&amp;api_key=…</c> redirects back with a
/// <c>request_token</c>, which <c>POST /session/token</c> exchanges for the day's access token. The request
/// carries <c>checksum = SHA-256(api_key + request_token + api_secret)</c>, never the secret itself.
/// </summary>
internal sealed class ZerodhaSignIn : IBrokerSignIn
{
    public BrokerKind Broker => BrokerKind.Zerodha;

    public SignInStyle Style => SignInStyle.Browser;

    public string? SignInUrl(BrokerCredential app, string redirectUri) =>
        $"https://kite.zerodha.com/connect/login?v=3&api_key={Uri.EscapeDataString(app.Key.Trim())}";

    public async Task<SessionIssue> SignInAsync(
        HttpClient http, BrokerCredential app, string proof, string redirectUri, DateTimeOffset now, CancellationToken ct)
    {
        var requestToken = SignInProof.Parameter(proof, "request_token");
        if (requestToken.Length == 0) return SessionIssue.Refused("Paste the address Kite redirected to — it carries the request_token.");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["api_key"] = app.Key.Trim(),
            ["request_token"] = requestToken,
            ["checksum"] = Checksum(app.Key.Trim(), requestToken, app.Secret.Trim()),
        });
        var (status, root, body) = await SignInProof.PostAsync(http, "https://api.kite.trade/session/token", form, ct, ("X-Kite-Version", "3")).ConfigureAwait(false);

        return SignInProof.Text(root, "data.access_token") is { Length: > 0 } token
            ? SessionIssue.Issued(token, SignInProof.Text(root, "data.user_id") ?? string.Empty)
            : SignInProof.Refusal(status, body, SignInProof.Text(root, "message"));
    }

    internal static string Checksum(string apiKey, string requestToken, string apiSecret) =>
        SignInProof.Sha256Hex(apiKey + requestToken + apiSecret);
}
