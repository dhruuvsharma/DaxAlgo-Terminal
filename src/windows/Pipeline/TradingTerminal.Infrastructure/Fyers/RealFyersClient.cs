using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;

namespace TradingTerminal.Infrastructure.Fyers;

/// <summary>
/// Fyers API v3 over REST: depth (which also carries the touch) and history, polled.
///
/// <para><b>Why polled.</b> Fyers' live data socket speaks a proprietary binary protocol that only its own
/// SDKs implement; nothing about it is published to write a decoder from. Its REST endpoints are
/// documented, so L1 and L2 are read from <c>/data/depth</c> at the configured interval — kept inside
/// Fyers' 200-requests-a-minute limit — and bars from <c>/data/history</c>.</para>
///
/// <para><b>Symbols are Fyers' own</b> (<c>NSE:SBIN-EQ</c>, <c>NSE:NIFTY50-INDEX</c>). Requests carry
/// <c>Authorization: APP_ID:ACCESS_TOKEN</c>.</para>
///
/// <para>Hosts and paths are from Fyers' v3 SDK and support articles; the depth and history field names
/// (<c>bids</c>/<c>ask</c> of <c>price</c>/<c>volume</c>/<c>ord</c>, <c>candles</c> of
/// <c>[epoch, o, h, l, c, v]</c>) could not be read from a published page and are pinned in the tests as
/// assumptions. Written 2026-09-25; not yet run against a real account.</para>
/// </summary>
internal sealed class RealFyersClient : RestBrokerClient<FyersOptions>
{
    public RealFyersClient(ILogger<RealFyersClient> logger, IOptions<FyersOptions> options, IBrokerCredentialSource credentials)
        : base(logger, options.Value, credentials) { }

    public override BrokerKind Kind => BrokerKind.Fyers;
    protected override string BrokerName => "Fyers";
    protected override string SignInAdvice => "Sign in to Fyers in the login window — its access tokens expire each day.";

    private HttpRequestMessage Get(string pathAndQuery)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Options.RestBaseUrl + pathAndQuery);
        var c = Credential;
        request.Headers.TryAddWithoutValidation("Authorization", $"{c.Key}:{c.Session}");
        return request;
    }

    /// <summary>Fyers answers many errors with HTTP 200 and <c>"s":"error"</c>.</summary>
    private static void ThrowIfError(JsonElement root)
    {
        if (SignInProof.Text(root, "s") is "error")
            throw new InvalidOperationException($"Fyers: {SignInProof.Text(root, "message") ?? "error"} (code {SignInProof.Text(root, "code")})");
    }

    protected override async Task CheckSessionAsync(CancellationToken ct)
    {
        var (doc, _) = await SendJsonAsync(() => Get("/api/v3/profile"), ct).ConfigureAwait(false);
        using (doc) ThrowIfError(doc.RootElement);
    }

    // ── Book ────────────────────────────────────────────────────────────────────────────────────

    private async Task<DepthSnapshot?> FetchDepthAsync(string symbol, CancellationToken ct)
    {
        var (doc, _) = await SendJsonAsync(() => Get($"/data/depth?symbol={Uri.EscapeDataString(symbol)}&ohlcv_flag=1"), ct).ConfigureAwait(false);
        using (doc)
        {
            ThrowIfError(doc.RootElement);
            return ParseDepth(doc.RootElement, symbol, Options.SizeScale);
        }
    }

    /// <summary><c>{"s":"ok","d":{"NSE:SBIN-EQ":{"bids":[{"price","volume","ord"}…],"ask":[…],"ltt":…}}}</c>.</summary>
    internal static DepthSnapshot? ParseDepth(JsonElement root, string symbol, double scale)
    {
        if (!root.TryGetProperty("d", out var d) || d.ValueKind != JsonValueKind.Object) return null;
        if (!d.TryGetProperty(symbol, out var entry))
        {
            // One symbol was asked for; take whatever single entry came back if the key is spelled differently.
            using var only = d.EnumerateObject();
            if (!only.MoveNext()) return null;
            entry = only.Current.Value;
        }

        var bids = Levels(entry, "bids", scale);
        var asks = Levels(entry, "ask", scale);
        if (asks.Count == 0) asks = Levels(entry, "asks", scale);
        var time = entry.TryGetProperty("ltt", out var ltt) && ltt.ValueKind == JsonValueKind.Number
            ? Crypto.CryptoConvert.MsUtc(ltt.GetInt64())
            : DateTime.UtcNow;
        return bids.Count == 0 && asks.Count == 0 ? null : new DepthSnapshot(time, bids, asks);
    }

    private static List<DepthLevel> Levels(JsonElement entry, string side, double scale)
    {
        var levels = new List<DepthLevel>();
        if (!entry.TryGetProperty(side, out var arr) || arr.ValueKind != JsonValueKind.Array) return levels;
        foreach (var level in arr.EnumerateArray())
        {
            var price = Crypto.CryptoConvert.D(level, "price");
            var volume = Crypto.CryptoConvert.D(level, "volume");
            if (price > 0 && volume > 0) levels.Add(new DepthLevel(price, (long)Math.Round(volume * scale)));
        }

        return levels;
    }

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default)
    {
        var symbol = Symbol(contract);
        return Poll("depth", async token => await FetchDepthAsync(symbol, token).ConfigureAwait(false) is { } d
            ? d with { Bids = [.. d.Bids.Take(levels)], Asks = [.. d.Asks.Take(levels)] }
            : null, ct);
    }

    public override IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default)
    {
        var symbol = Symbol(contract);
        return Poll("quotes", async token => await FetchDepthAsync(symbol, token).ConfigureAwait(false) is { Bids.Count: > 0, Asks.Count: > 0 } d
            ? new Tick(d.TimestampUtc, d.BestBid, d.BestAsk, d.BestBidSize, d.BestAskSize)
            : null, ct);
    }

    // ── History ─────────────────────────────────────────────────────────────────────────────────

    protected override bool HasInterval(BarSize size) => true;

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, TimeSpan span, CancellationToken ct)
    {
        var to = DateTimeOffset.UtcNow;
        var cap = size == BarSize.OneDay ? TimeSpan.FromDays(366) : TimeSpan.FromDays(100);
        var from = to - TimeSpan.FromTicks(Math.Min(Math.Max(span.Ticks, TimeSpan.FromDays(1).Ticks), cap.Ticks));
        var query = $"/data/history?symbol={Uri.EscapeDataString(symbol)}&resolution={Resolution(size)}&date_format=0"
            + $"&range_from={from.ToUnixTimeSeconds()}&range_to={to.ToUnixTimeSeconds()}&cont_flag=1";
        var (doc, body) = await SendJsonAsync(() => Get(query), ct).ConfigureAwait(false);
        using (doc)
        {
            ThrowIfError(doc.RootElement);
            return WireFormat.OrWarn(ParseCandles(doc.RootElement, Options.SizeScale), body, Logger, BrokerName, "candles");
        }
    }

    /// <summary><c>{"s":"ok","candles":[[epoch, o, h, l, c, v], …]}</c>.</summary>
    internal static IReadOnlyList<Bar> ParseCandles(JsonElement root, double scale)
    {
        var bars = new List<Bar>();
        if (!root.TryGetProperty("candles", out var candles) || candles.ValueKind != JsonValueKind.Array) return bars;
        foreach (var row in candles.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 6)
                bars.Add(new Bar(
                    Crypto.CryptoConvert.MsUtc((long)row[0].GetDouble()),
                    row[1].GetDouble(), row[2].GetDouble(), row[3].GetDouble(), row[4].GetDouble(),
                    (long)Math.Round(row[5].GetDouble() * scale)));
        return bars;
    }

    internal static string Resolution(BarSize size) => size switch
    {
        BarSize.OneMinute => "1",
        BarSize.ThreeMinutes => "3",
        BarSize.FiveMinutes => "5",
        BarSize.FifteenMinutes => "15",
        BarSize.OneHour => "60",
        BarSize.OneDay => "D",
        _ => "1",
    };
}

/// <summary>
/// Fyers v3 sign-in: <c>generate-authcode</c> redirects back with an <c>auth_code</c>, which
/// <c>validate-authcode</c> exchanges for the day's access token. The request carries
/// <c>appIdHash = SHA-256(app_id + ":" + secret)</c>, never the secret itself.
/// Stored: key = app id (<c>XXXX-100</c>), secret = app secret, session = access token.
/// </summary>
internal sealed class FyersSignIn : IBrokerSignIn
{
    public BrokerKind Broker => BrokerKind.Fyers;

    public SignInStyle Style => SignInStyle.Browser;

    public string? SignInUrl(BrokerCredential app, string redirectUri) =>
        "https://api-t1.fyers.in/api/v3/generate-authcode"
        + $"?client_id={Uri.EscapeDataString(app.Key.Trim())}&redirect_uri={Uri.EscapeDataString(redirectUri)}"
        + "&response_type=code&state=daxalgo";

    public async Task<SessionIssue> SignInAsync(
        HttpClient http, BrokerCredential app, string proof, string redirectUri, DateTimeOffset now, CancellationToken ct)
    {
        var code = SignInProof.Parameter(proof, "auth_code");
        if (code.Length == 0) return SessionIssue.Refused("Paste the address Fyers redirected to — it carries the auth_code.");

        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["appIdHash"] = AppIdHash(app.Key.Trim(), app.Secret.Trim()),
            ["code"] = code,
        });
        var (status, root, body) = await SignInProof.PostAsync(http, "https://api-t1.fyers.in/api/v3/validate-authcode",
            new StringContent(json, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);

        return SignInProof.Text(root, "access_token") is { Length: > 0 } token
            ? SessionIssue.Issued(token)
            : SignInProof.Refusal(status, body, SignInProof.Text(root, "message"));
    }

    internal static string AppIdHash(string appId, string secret) => SignInProof.Sha256Hex($"{appId}:{secret}");
}
