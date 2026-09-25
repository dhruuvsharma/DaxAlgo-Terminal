using System.Buffers.Binary;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;

namespace TradingTerminal.Infrastructure.Dhan;

/// <summary>
/// Dhan (DhanHQ v2): history over REST, quotes and depth over the binary live feed.
///
/// <para><b>Symbols are <c>SEGMENT:SECURITYID</c></b> (<c>NSE_EQ:1333</c> is HDFC Bank). Dhan's REST calls
/// name the segment as text; its feed names it as a number in each packet's header.</para>
///
/// <para><b>Live feed</b> (<c>wss://api-feed.dhan.co?version=2&amp;…</c>): one socket for everything (Dhan allows
/// five), full-packet subscriptions (request code 21). Packets are little-endian: an eight-byte header —
/// response code, length, segment, security id — then the body. The full packet (code 8) carries the last
/// trade, day statistics and five depth levels of twenty bytes: bid quantity, ask quantity, bid orders, ask
/// orders, bid price, ask price. Prices are float32 rupees. The server pings every ten seconds and the
/// socket answers on its own.</para>
///
/// <para>Offsets are taken from Dhan's official Python client's <c>struct</c> formats, which are what it
/// actually decodes with; the documentation's table disagrees with them by a byte in the full packet's
/// length. Written 2026-09-25; not yet run against a real account.</para>
/// </summary>
internal sealed class RealDhanClient : RestBrokerClient<DhanOptions>
{
    private readonly SharedFeed<DhanPacket> _feed;

    public RealDhanClient(ILogger<RealDhanClient> logger, IOptions<DhanOptions> options, IBrokerCredentialSource credentials)
        : base(logger, options.Value, credentials)
    {
        _feed = new SharedFeed<DhanPacket>(new FeedProtocol<DhanPacket>
        {
            Name = "Dhan",
            Endpoint = _ =>
            {
                var c = Credential;
                return Task.FromResult(FeedEndpoint.At(
                    $"{Options.WsBaseUrl}?version=2&token={Uri.EscapeDataString(c.Session)}&clientId={Uri.EscapeDataString(c.Account)}&authType=2"));
            },
            OnConnect = keys => keys.Chunk(100).Select(chunk => SubscribeMessage(chunk)),
            OnAdd = key => [SubscribeMessage([key])],
            Decode = frame => DecodeFrame(frame).Select(p => new KeyValuePair<string, DhanPacket>(p.Key, p)),
            InitialDelaySeconds = Options.ReconnectInitialDelaySeconds,
            MaxDelaySeconds = Options.ReconnectMaxDelaySeconds,
        }, logger);
    }

    public override BrokerKind Kind => BrokerKind.Dhan;
    protected override string BrokerName => "Dhan";
    protected override string SignInAdvice => "Generate an access token on Dhan's site and paste it in the login window.";

    /// <summary>Dhan's numeric segment codes, as its feed writes them.</summary>
    internal static int SegmentCode(string segment) => segment.ToUpperInvariant() switch
    {
        "IDX_I" => 0,
        "NSE_EQ" => 1,
        "NSE_FNO" => 2,
        "NSE_CURRENCY" => 3,
        "BSE_EQ" => 4,
        "MCX_COMM" => 5,
        "BSE_CURRENCY" => 7,
        "BSE_FNO" => 8,
        _ => 1,
    };

    internal static string SegmentName(int code) => code switch
    {
        0 => "IDX_I",
        1 => "NSE_EQ",
        2 => "NSE_FNO",
        3 => "NSE_CURRENCY",
        4 => "BSE_EQ",
        5 => "MCX_COMM",
        7 => "BSE_CURRENCY",
        8 => "BSE_FNO",
        _ => "NSE_EQ",
    };

    private static (string Segment, string SecurityId) Split(string symbol)
    {
        var colon = symbol.IndexOf(':');
        return colon > 0 ? (symbol[..colon].ToUpperInvariant(), symbol[(colon + 1)..]) : ("NSE_EQ", symbol);
    }

    protected override string ExchangeOf(string symbol) => Split(symbol).Segment.Split('_')[0];

    /// <summary>The feed's dispatch key: segment name and security id.</summary>
    private static string Key(string symbol)
    {
        var (segment, id) = Split(symbol);
        return $"{segment}:{id}";
    }

    private static string SubscribeMessage(IReadOnlyCollection<string> keys)
    {
        var list = keys.Select(k => k.Split(':')).Select(p => $"{{\"ExchangeSegment\":\"{p[0]}\",\"SecurityId\":\"{p[1]}\"}}");
        // 21 is the full-packet subscription: last trade, day statistics and five depth levels.
        return $"{{\"RequestCode\":21,\"InstrumentCount\":{keys.Count},\"InstrumentList\":[{string.Join(',', list)}]}}";
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? json = null)
    {
        var request = new HttpRequestMessage(method, Options.RestBaseUrl + path);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var c = Credential;
        request.Headers.TryAddWithoutValidation("access-token", c.Session);
        request.Headers.TryAddWithoutValidation("client-id", c.Account);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        return request;
    }

    protected override async Task CheckSessionAsync(CancellationToken ct)
    {
        var (doc, _) = await SendJsonAsync(() => Request(HttpMethod.Get, "/fundlimit"), ct).ConfigureAwait(false);
        doc.Dispose();
    }

    // ── History ─────────────────────────────────────────────────────────────────────────────────

    protected override bool HasInterval(BarSize size) => size != BarSize.ThreeMinutes;

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, TimeSpan span, CancellationToken ct)
    {
        var (segment, id) = Split(symbol);
        var instrument = segment == "IDX_I" ? "INDEX" : "EQUITY";
        var to = DateTime.UtcNow;
        string path, json;

        if (size == BarSize.OneDay)
        {
            var from = to - TimeSpan.FromTicks(Math.Max(span.Ticks, TimeSpan.FromDays(5).Ticks));
            path = "/charts/historical";
            json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["securityId"] = id, ["exchangeSegment"] = segment, ["instrument"] = instrument,
                ["expiryCode"] = 0, ["oi"] = false,
                ["fromDate"] = IndiaTime.Format(from, "yyyy-MM-dd"), ["toDate"] = IndiaTime.Format(to.AddDays(1), "yyyy-MM-dd"),
            });
        }
        else
        {
            // At most ninety days of intraday data per request.
            var from = to - TimeSpan.FromTicks(Math.Min(Math.Max(span.Ticks, TimeSpan.FromDays(1).Ticks), TimeSpan.FromDays(90).Ticks));
            path = "/charts/intraday";
            json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["securityId"] = id, ["exchangeSegment"] = segment, ["instrument"] = instrument,
                ["interval"] = Interval(size), ["oi"] = false,
                ["fromDate"] = IndiaTime.Format(from, "yyyy-MM-dd HH:mm:ss"), ["toDate"] = IndiaTime.Format(to, "yyyy-MM-dd HH:mm:ss"),
            });
        }

        var (doc, body) = await SendJsonAsync(() => Request(HttpMethod.Post, path, json), ct).ConfigureAwait(false);
        using (doc)
            return WireFormat.OrWarn(ParseCandles(doc.RootElement, Options.SizeScale), body, Logger, BrokerName, "candles");
    }

    /// <summary><c>{"open":[…],"high":[…],"low":[…],"close":[…],"volume":[…],"timestamp":[…]}</c> — parallel
    /// arrays, timestamps in Unix seconds.</summary>
    internal static IReadOnlyList<Bar> ParseCandles(JsonElement root, double scale)
    {
        var bars = new List<Bar>();
        var source = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;
        if (!Array(source, "open", out var open) || !Array(source, "high", out var high) || !Array(source, "low", out var low)
            || !Array(source, "close", out var close) || !Array(source, "timestamp", out var time))
            return bars;
        Array(source, "volume", out var volume);

        var n = new[] { open.Count, high.Count, low.Count, close.Count, time.Count }.Min();
        for (var i = 0; i < n; i++)
            bars.Add(new Bar(
                Crypto.CryptoConvert.MsUtc((long)time[i]),
                open[i], high[i], low[i], close[i],
                i < volume.Count ? (long)Math.Round(volume[i] * scale) : 0));
        return bars;
    }

    private static bool Array(JsonElement obj, string name, out List<double> values)
    {
        values = [];
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return false;
        foreach (var v in arr.EnumerateArray()) values.Add(v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0);
        return true;
    }

    internal static string Interval(BarSize size) => size switch
    {
        BarSize.OneMinute => "1",
        BarSize.FiveMinutes => "5",
        BarSize.FifteenMinutes => "15",
        BarSize.OneHour => "60",
        _ => "1",
    };

    // ── Live feed ───────────────────────────────────────────────────────────────────────────────

    public override async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var p in _feed.SubscribeAsync(Key(Symbol(contract)), ct).ConfigureAwait(false))
            yield return p.Bids.Count > 0 && p.Asks.Count > 0
                ? new Tick(p.TimeUtc, p.Bids[0].Price, p.Asks[0].Price, p.Bids[0].Size, p.Asks[0].Size)
                : new Tick(p.TimeUtc, p.LastPrice, p.LastPrice, 0, 0);
    }

    public override async IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var p in _feed.SubscribeAsync(Key(Symbol(contract)), ct).ConfigureAwait(false))
            if (p.Bids.Count > 0 || p.Asks.Count > 0)
                yield return new DepthSnapshot(p.TimeUtc, [.. p.Bids.Take(levels)], [.. p.Asks.Take(levels)]);
    }

    internal sealed record DhanPacket(string Key, double LastPrice, DateTime TimeUtc, IReadOnlyList<DepthLevel> Bids, IReadOnlyList<DepthLevel> Asks);

    /// <summary>Body length after the eight-byte header, by response code.</summary>
    private static int PacketLength(byte code) => code switch
    {
        2 => 16,    // ticker
        4 => 50,    // quote
        5 => 12,    // open interest
        6 => 16,    // previous close
        8 => 162,   // full
        50 => 10,   // disconnect
        _ => 0,
    };

    /// <summary>Decodes a frame, which may hold one packet or several back to back.</summary>
    internal static IEnumerable<DhanPacket> DecodeFrame(byte[] frame)
    {
        var offset = 0;
        while (offset + 8 <= frame.Length)
        {
            var code = frame[offset];
            var length = PacketLength(code);
            if (length == 0 || offset + length > frame.Length) yield break;
            if (DecodePacket(frame.AsSpan(offset, length)) is { } packet) yield return packet;
            offset += length;
        }
    }

    internal static DhanPacket? DecodePacket(ReadOnlySpan<byte> p)
    {
        var code = p[0];
        var segment = p[3];
        var securityId = BinaryPrimitives.ReadInt32LittleEndian(p.Slice(4, 4));
        var key = $"{SegmentName(segment)}:{securityId}";
        float F(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadSingleLittleEndian(s.Slice(at, 4));
        int I(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadInt32LittleEndian(s.Slice(at, 4));
        DateTime Time(int seconds) => seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : DateTime.UtcNow;

        switch (code)
        {
            case 2 when p.Length >= 16:
                return new DhanPacket(key, F(p, 8), Time(I(p, 12)), [], []);

            case 4 when p.Length >= 50:
                return new DhanPacket(key, F(p, 8), Time(I(p, 14)), [], []);

            case 8 when p.Length >= 162:
            {
                var bids = new List<DepthLevel>(5);
                var asks = new List<DepthLevel>(5);
                for (var level = 0; level < 5; level++)
                {
                    var at = 62 + level * 20;
                    var bidQty = I(p, at);
                    var askQty = I(p, at + 4);
                    var bidPrice = F(p, at + 12);
                    var askPrice = F(p, at + 16);
                    if (bidPrice > 0 && bidQty > 0) bids.Add(new DepthLevel(bidPrice, bidQty));
                    if (askPrice > 0 && askQty > 0) asks.Add(new DepthLevel(askPrice, askQty));
                }

                return new DhanPacket(key, F(p, 8), Time(I(p, 14)), bids, asks);
            }

            default:
                return null;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await _feed.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Dhan has no exchange step: the access token is generated on Dhan's site. "Signing in" checks it with
/// one call (<c>/fundlimit</c>) so a mistyped or expired token is caught at the form rather than at the
/// first chart. Stored: account = client id, session = access token.
/// </summary>
internal sealed class DhanSignIn : IBrokerSignIn
{
    public BrokerKind Broker => BrokerKind.Dhan;

    public SignInStyle Style => SignInStyle.Token;

    public async Task<SessionIssue> SignInAsync(
        HttpClient http, BrokerCredential app, string proof, string redirectUri, DateTimeOffset now, CancellationToken ct)
    {
        var token = proof.Trim();
        if (token.Length == 0) return SessionIssue.Refused("Paste the access token generated on Dhan's site.");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.dhan.co/v2/fundlimit");
        request.Headers.TryAddWithoutValidation("access-token", token);
        request.Headers.TryAddWithoutValidation("client-id", app.Account.Trim());
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        var (status, root, body) = await SignInProof.SendAsync(http, request, ct).ConfigureAwait(false);

        return status is >= 200 and < 300
            ? SessionIssue.Issued(token, app.Account.Trim())
            : SignInProof.Refusal(status, body, SignInProof.Text(root, "errorMessage"), SignInProof.Text(root, "internalErrorMessage"));
    }
}
