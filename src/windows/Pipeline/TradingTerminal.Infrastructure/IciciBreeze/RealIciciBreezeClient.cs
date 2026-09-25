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

namespace TradingTerminal.Infrastructure.IciciBreeze;

/// <summary>
/// ICICI Direct Breeze over REST: quotes (the touch) polled, history from the v2 charts API.
///
/// <para><b>Symbols are <c>EXCHANGE:STOCKCODE</c></b>, the stock code being ICICI's own — <c>RELIND</c> for
/// Reliance, <c>INFTEC</c> for Infosys — not the exchange symbol.</para>
///
/// <para><b>Every v1 request is signed</b>: <c>X-Checksum: token SHA-256(timestamp + body + secret)</c>,
/// with the ISO timestamp in <c>X-Timestamp</c>, the app key and the session token beside it. Quotes are a
/// GET <i>with a JSON body</i>, which is unusual and is what Breeze's own client sends.</para>
///
/// <para><b>Why polled.</b> Breeze streams over socket.io with a token-and-code subscription scheme that is
/// only described by its SDK's source; its quotes endpoint is documented, so the touch is polled inside
/// Breeze's hundred-calls-a-minute limit. Breeze publishes no full book through that endpoint, so depth is
/// the one level the quote carries.</para>
///
/// <para>Written from Breeze's official Python client (hosts, signing, payloads), 2026-09-25. Response
/// field names are assumptions pinned in the tests. Not yet run against a real account.</para>
/// </summary>
internal sealed class RealIciciBreezeClient : RestBrokerClient<IciciBreezeOptions>
{
    public RealIciciBreezeClient(ILogger<RealIciciBreezeClient> logger, IOptions<IciciBreezeOptions> options, IBrokerCredentialSource credentials)
        : base(logger, options.Value, credentials) { }

    public override BrokerKind Kind => BrokerKind.IciciBreeze;
    protected override string BrokerName => "ICICI Breeze";
    protected override string SignInAdvice => "Sign in to ICICI Breeze in the login window — its sessions last one day.";

    private static (string Exchange, string Code) Split(string symbol)
    {
        var colon = symbol.IndexOf(':');
        return colon > 0 ? (symbol[..colon].ToUpperInvariant(), symbol[(colon + 1)..].ToUpperInvariant()) : ("NSE", symbol.ToUpperInvariant());
    }

    /// <summary>A signed v1 request.</summary>
    private HttpRequestMessage Signed(HttpMethod method, string path, string json)
    {
        var c = Credential;
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + ".000Z";
        var request = new HttpRequestMessage(method, $"{Options.RestBaseUrl}/{path}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Checksum", "token " + Checksum(stamp, json, c.Secret.Trim()));
        request.Headers.TryAddWithoutValidation("X-Timestamp", stamp);
        request.Headers.TryAddWithoutValidation("X-AppKey", c.Key.Trim());
        request.Headers.TryAddWithoutValidation("X-SessionToken", c.Session);
        return request;
    }

    internal static string Checksum(string timestamp, string body, string secret) => SignInProof.Sha256Hex(timestamp + body + secret);

    /// <summary>Breeze wraps answers as <c>{"Success":…,"Status":200,"Error":null}</c> and reports failures in
    /// <c>Error</c>, sometimes under HTTP 200.</summary>
    private static void ThrowIfError(JsonElement root)
    {
        if (root.TryGetProperty("Error", out var error) && error.ValueKind == JsonValueKind.String && error.GetString() is { Length: > 0 } text)
            throw new InvalidOperationException($"ICICI Breeze: {text}");
    }

    protected override async Task CheckSessionAsync(CancellationToken ct)
    {
        var (doc, _) = await SendJsonAsync(() => Signed(HttpMethod.Get, "funds", "{}"), ct).ConfigureAwait(false);
        using (doc) ThrowIfError(doc.RootElement);
    }

    // ── Quotes ──────────────────────────────────────────────────────────────────────────────────

    private async Task<Tick?> FetchQuoteAsync(string symbol, CancellationToken ct)
    {
        var (exchange, code) = Split(symbol);
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["stock_code"] = code, ["exchange_code"] = exchange, ["expiry_date"] = "",
            ["product_type"] = "cash", ["right"] = "", ["strike_price"] = "",
        });
        var (doc, _) = await SendJsonAsync(() => Signed(HttpMethod.Get, "quotes", json), ct).ConfigureAwait(false);
        using (doc)
        {
            ThrowIfError(doc.RootElement);
            return ParseQuote(doc.RootElement, exchange);
        }
    }

    /// <summary><c>{"Success":[{"exchange_code","ltp","ltt","best_bid_price","best_bid_quantity","best_offer_price","best_offer_quantity"}…]}</c>.</summary>
    internal static Tick? ParseQuote(JsonElement root, string exchange)
    {
        if (!root.TryGetProperty("Success", out var rows) || rows.ValueKind != JsonValueKind.Array) return null;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.TryGetProperty("exchange_code", out var ex) && !string.Equals(ex.GetString(), exchange, StringComparison.OrdinalIgnoreCase))
                continue;
            var bid = Crypto.CryptoConvert.D(row, "best_bid_price");
            var ask = Crypto.CryptoConvert.D(row, "best_offer_price");
            var last = Crypto.CryptoConvert.D(row, "ltp");
            var time = row.TryGetProperty("ltt", out var ltt) ? IndiaTime.ToUtc(ltt.GetString()) : null;
            return new Tick(time ?? DateTime.UtcNow,
                bid > 0 ? bid : last, ask > 0 ? ask : last,
                (long)Crypto.CryptoConvert.D(row, "best_bid_quantity"), (long)Crypto.CryptoConvert.D(row, "best_offer_quantity"));
        }

        return null;
    }

    public override IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default)
    {
        var symbol = Symbol(contract);
        return Poll("quotes", token => FetchQuoteAsync(symbol, token), ct);
    }

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default)
    {
        var symbol = Symbol(contract);
        return Poll("quotes", async token => await FetchQuoteAsync(symbol, token).ConfigureAwait(false) is { } t
            ? new DepthSnapshot(t.TimestampUtc,
                t.BidSize > 0 ? [new DepthLevel(t.Bid, t.BidSize)] : [],
                t.AskSize > 0 ? [new DepthLevel(t.Ask, t.AskSize)] : [])
            : null, ct);
    }

    // ── History ─────────────────────────────────────────────────────────────────────────────────

    protected override bool HasInterval(BarSize size) => size is BarSize.OneMinute or BarSize.FiveMinutes or BarSize.OneDay;

    /// <summary>Breeze serves 1m, 5m, 30m and 1d: 15m rolls up from 5m; 3m and 1h from 1m (30m is not a
    /// size the terminal asks for).</summary>
    protected override BarSize RollupBase(BarSize size) => size == BarSize.FifteenMinutes ? BarSize.FiveMinutes : BarSize.OneMinute;

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, TimeSpan span, CancellationToken ct)
    {
        var (exchange, code) = Split(symbol);
        var to = DateTime.UtcNow;
        var from = to - TimeSpan.FromTicks(Math.Max(span.Ticks, TimeSpan.FromDays(size == BarSize.OneDay ? 10 : 2).Ticks));
        var interval = size switch { BarSize.FiveMinutes => "5minute", BarSize.OneDay => "1day", _ => "1minute" };
        var url = $"{Options.HistoryBaseUrl}/historicalcharts?stock_code={code}&exch_code={exchange}"
            + $"&from_date={Iso(from)}&to_date={Iso(to)}&interval={interval}&product_type=cash";
        var (doc, body) = await SendJsonAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("X-SessionToken", Credential.Session);
            request.Headers.TryAddWithoutValidation("apikey", Credential.Key.Trim());
            return request;
        }, ct).ConfigureAwait(false);
        using (doc)
        {
            ThrowIfError(doc.RootElement);
            return WireFormat.OrWarn(ParseCandles(doc.RootElement, Options.SizeScale), body, Logger, BrokerName, "candles");
        }
    }

    private static string Iso(DateTime utc) => Uri.EscapeDataString(utc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + ".000Z");

    /// <summary><c>{"Success":[{"datetime":"2022-08-15 09:15:00","open","high","low","close","volume"}…]}</c>, times IST.</summary>
    internal static IReadOnlyList<Bar> ParseCandles(JsonElement root, double scale)
    {
        var bars = new List<Bar>();
        if (!root.TryGetProperty("Success", out var rows) || rows.ValueKind != JsonValueKind.Array) return bars;
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("datetime", out var dt) || IndiaTime.ToUtc(dt.GetString()) is not { } time) continue;
            bars.Add(new Bar(time,
                Crypto.CryptoConvert.D(row, "open"), Crypto.CryptoConvert.D(row, "high"),
                Crypto.CryptoConvert.D(row, "low"), Crypto.CryptoConvert.D(row, "close"),
                (long)Math.Round(Crypto.CryptoConvert.D(row, "volume") * scale)));
        }

        return bars;
    }
}

/// <summary>
/// Breeze sign-in: <c>api.icicidirect.com/apiuser/login?api_key=…</c> redirects back with an
/// <c>apisession</c>, which <c>customerdetails</c> (a GET with a JSON body) exchanges for the base64 session
/// token every later request carries. Stored: key = app key, secret = secret key, session = session token.
/// </summary>
internal sealed class IciciBreezeSignIn : IBrokerSignIn
{
    public BrokerKind Broker => BrokerKind.IciciBreeze;

    public SignInStyle Style => SignInStyle.Browser;

    public string? SignInUrl(BrokerCredential app, string redirectUri) =>
        $"https://api.icicidirect.com/apiuser/login?api_key={Uri.EscapeDataString(app.Key.Trim())}";

    public async Task<SessionIssue> SignInAsync(
        HttpClient http, BrokerCredential app, string proof, string redirectUri, DateTimeOffset now, CancellationToken ct)
    {
        var apiSession = SignInProof.Parameter(proof, "apisession");
        if (apiSession.Length == 0) return SessionIssue.Refused("Paste the address ICICI redirected to — it carries the apisession.");

        var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["SessionToken"] = apiSession, ["AppKey"] = app.Key.Trim() });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.icicidirect.com/breezeapi/api/v1/customerdetails")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var (status, root, body) = await SignInProof.SendAsync(http, request, ct).ConfigureAwait(false);

        return SignInProof.Text(root, "Success.session_token") is { Length: > 0 } session
            ? SessionIssue.Issued(session, SignInProof.Text(root, "Success.idirect_userid") ?? string.Empty)
            : SignInProof.Refusal(status, body, SignInProof.Text(root, "Error"));
    }
}
