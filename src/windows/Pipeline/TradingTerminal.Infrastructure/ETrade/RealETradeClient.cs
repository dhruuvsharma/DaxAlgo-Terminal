using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.ETrade;

/// <summary>
/// E*TRADE: quotes polled over its OAuth 1.0a REST API.
///
/// <para><b>Session.</b> A request token, authorised on E*TRADE's page, is exchanged with the verification
/// code the page shows for an access token and its secret. The token goes idle after two hours without a
/// call — the keeper renews it every ninety minutes — and dies at midnight US Eastern whatever happens, after
/// which only a new sign-in helps.</para>
///
/// <para><b>No history.</b> E*TRADE's API publishes no bars at all, so a chart has no past and its forming
/// bar is built from the polled quotes (<see cref="QuoteBars"/>, approximate by design). No depth either.</para>
///
/// <para>Paths and fields are from E*TRADE's developer documentation. Written 2026-09-25; not yet run
/// against a real account.</para>
/// </summary>
internal sealed class RealETradeClient : KeptSessionClient<ETradeOptions>
{
    private readonly TimeProvider _time;
    private int _warnedHistory;

    public RealETradeClient(
        ILogger<RealETradeClient> logger, IOptions<ETradeOptions> options, IBrokerCredentialSource credentials, IBrokerSessionStore store,
        TimeProvider? time = null)
        : base(logger, options.Value, credentials, store, BrokerKind.ETrade)
    {
        _time = time ?? TimeProvider.System;
    }

    public override BrokerKind Kind => BrokerKind.ETrade;
    protected override string BrokerName => "E*TRADE";
    protected override string SignInAdvice => "Sign in to E*TRADE in the login window — its sessions end at midnight US Eastern.";
    protected override string CurrencyOf(string symbol) => "USD";
    protected override string ExchangeOf(string symbol) => "SMART";

    /// <summary>OAuth 1.0a signs every request with the consumer key and secret and the access token and secret.</summary>
    protected override void Authorize(HttpRequestMessage request, KeptSession session)
    {
        var app = Credential;
        request.Headers.TryAddWithoutValidation("Authorization", OAuth1.Authorization(
            request.Method.Method, request.RequestUri!.AbsoluteUri, app.Key.Trim(), app.Secret.Trim(), session.AccessToken, session.Secret,
            _time.GetUtcNow().ToUnixTimeSeconds(), OAuth1.Nonce()));
    }

    /// <summary>Renewal keeps the same token; it only restarts the two-hour idle clock.</summary>
    protected override async Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct)
    {
        var url = $"{Options.RestBaseUrl}/oauth/renew_access_token";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", OAuth1.Authorization(
            "GET", url, app.Key.Trim(), app.Secret.Trim(), session.AccessToken, session.Secret, _time.GetUtcNow().ToUnixTimeSeconds(), OAuth1.Nonce()));
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"E*TRADE would not renew the session (HTTP {(int)response.StatusCode}) — sessions end at midnight US Eastern; sign in again. {Snippet(body)}");
        return session with { ExpiresUtc = _time.GetUtcNow() + ETradeSignIn.Renewal };
    }

    private static string Snippet(string body) => SignInProof.Snippet(body);

    protected override async Task CheckSessionAsync(CancellationToken ct) => _ = await FetchQuoteAsync("SPY", ct).ConfigureAwait(false);

    // ── Quotes ──────────────────────────────────────────────────────────────────────────────────

    private async Task<PolledQuote?> FetchQuoteAsync(string symbol, CancellationToken ct)
    {
        var (doc, body) = await GetAsync($"{Options.RestBaseUrl}/v1/market/quote/{Uri.EscapeDataString(symbol)}.json?detailFlag=ALL", ct).ConfigureAwait(false);
        using (doc)
        {
            var quote = ParseQuote(doc.RootElement, Options.SizeScale);
            if (quote is null && body.Length > 64) Logger.LogWarning("E*TRADE quote for {Symbol} did not parse: {Body}", symbol, Snippet(body));
            return quote;
        }
    }

    /// <summary><c>{"QuoteResponse":{"QuoteData":[{"dateTimeUTC":epochSeconds,"All":{"bid","ask","bidSize",
    /// "askSize","lastTrade","totalVolume",…}}]}}</c>. A refused symbol comes back as <c>Messages</c> instead.</summary>
    internal static PolledQuote? ParseQuote(JsonElement root, double scale)
    {
        if (!root.TryGetProperty("QuoteResponse", out var response)
            || !response.TryGetProperty("QuoteData", out var data)
            || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            return null;

        var q = data[0];
        if (!q.TryGetProperty("All", out var all)) return null;
        var time = q.TryGetProperty("dateTimeUTC", out var t) ? CryptoConvert.MsUtc(CryptoConvert.L(t)) : DateTime.UtcNow;
        return new PolledQuote(time,
            CryptoConvert.D(all, "bid"), CryptoConvert.D(all, "ask"),
            (long)Math.Round(CryptoConvert.D(all, "bidSize") * scale), (long)Math.Round(CryptoConvert.D(all, "askSize") * scale),
            CryptoConvert.D(all, "lastTrade"), CryptoConvert.D(all, "totalVolume"));
    }

    private IAsyncEnumerable<PolledQuote> Quotes(Contract contract, CancellationToken ct)
    {
        var symbol = Symbol(contract);
        return Poll("quotes", token => FetchQuoteAsync(symbol, token), ct);
    }

    public override async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var quote in Quotes(contract, ct).ConfigureAwait(false))
            if (quote.ToTick() is { } tick) yield return tick;
    }

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default) =>
        throw new NotSupportedException("E*TRADE's API publishes the touch only — no order-book depth.");

    // ── Bars ────────────────────────────────────────────────────────────────────────────────────

    protected override bool HasInterval(BarSize size) => true;

    /// <summary>E*TRADE has no bar history. An empty list, said once in the log, rather than a guess.</summary>
    protected override Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, TimeSpan span, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _warnedHistory, 1) == 0)
            Logger.LogInformation("E*TRADE's API has no bar history; charts start from the first quote and build bars from quotes.");
        return Task.FromResult<IReadOnlyList<Bar>>([]);
    }

    public override IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, CancellationToken ct = default) =>
        QuoteBarStreams.BuildAsync(Quotes(contract, ct), barSize, ct);
}

/// <summary>
/// E*TRADE's OAuth 1.0a sign-in. Opening the page takes a call first: a request token is fetched with
/// <c>oauth_callback=oob</c>, the page authorises it and shows a short verification code, and the code
/// exchanges the request token for an access token. The request token's secret is needed for that last
/// step, so it is held here, by consumer key, between the two.
/// </summary>
internal sealed class ETradeSignIn : IBrokerSignIn
{
    /// <summary>How often the session is renewed: inside E*TRADE's two-hour idle limit.</summary>
    public static readonly TimeSpan Renewal = TimeSpan.FromMinutes(90);

    private readonly ETradeOptions _options;
    private readonly ConcurrentDictionary<string, (string Token, string Secret)> _pending = new(StringComparer.Ordinal);

    public ETradeSignIn() : this(new ETradeOptions()) { }

    internal ETradeSignIn(ETradeOptions options) => _options = options;

    public BrokerKind Broker => BrokerKind.ETrade;

    public SignInStyle Style => SignInStyle.Browser;

    public async Task<string?> SignInUrlAsync(HttpClient http, BrokerCredential app, string redirectUri, DateTimeOffset now, CancellationToken ct)
    {
        var url = $"{_options.RestBaseUrl}/oauth/request_token";
        var form = await SignedGetAsync(http, url, app, token: "", tokenSecret: "", now, ct, ("oauth_callback", "oob")).ConfigureAwait(false);
        if (!form.TryGetValue("oauth_token", out var token) || !form.TryGetValue("oauth_token_secret", out var secret))
            throw new InvalidOperationException("E*TRADE issued no request token.");

        _pending[app.Key.Trim()] = (token, secret);
        return $"{_options.AuthorizeUrl}?key={Uri.EscapeDataString(app.Key.Trim())}&token={Uri.EscapeDataString(token)}";
    }

    public async Task<SessionIssue> SignInAsync(
        HttpClient http, BrokerCredential app, string proof, string redirectUri, DateTimeOffset now, CancellationToken ct)
    {
        var verifier = SignInProof.Parameter(proof, "oauth_verifier").Trim();
        if (verifier.Length == 0) return SessionIssue.Refused("Paste the verification code E*TRADE showed.");
        if (!_pending.TryRemove(app.Key.Trim(), out var request))
            return SessionIssue.Refused("Open E*TRADE's sign-in page from this row first — its code is tied to that page.");

        try
        {
            var form = await SignedGetAsync(http, $"{_options.RestBaseUrl}/oauth/access_token", app, request.Token, request.Secret, now, ct,
                ("oauth_verifier", verifier)).ConfigureAwait(false);
            if (!form.TryGetValue("oauth_token", out var token) || !form.TryGetValue("oauth_token_secret", out var secret))
                return SessionIssue.Refused("E*TRADE issued no access token.");
            return SessionIssue.Issued(new KeptSession { AccessToken = token, Secret = secret, ExpiresUtc = now + Renewal }.ToJson());
        }
        catch (InvalidOperationException ex)
        {
            return SessionIssue.Refused(ex.Message);
        }
    }

    private static async Task<Dictionary<string, string>> SignedGetAsync(
        HttpClient http, string url, BrokerCredential app, string token, string tokenSecret, DateTimeOffset now, CancellationToken ct,
        params (string Name, string Value)[] extra)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization",
            OAuth1.Authorization("GET", url, app.Key.Trim(), app.Secret.Trim(), token, tokenSecret, now.ToUnixTimeSeconds(), OAuth1.Nonce(), extra));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"E*TRADE refused (HTTP {(int)response.StatusCode}): {SignInProof.Snippet(body)}");
        return OAuth1.ReadForm(body);
    }
}
