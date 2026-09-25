using System.Globalization;
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

namespace TradingTerminal.Infrastructure.Ig;

/// <summary>
/// IG: market snapshots polled over its REST API, and history spent carefully.
///
/// <para><b>Session.</b> A credentials sign-in — API key, user name, password — returns two headers, <c>CST</c>
/// and <c>X-SECURITY-TOKEN</c>, which every request sends back. They last six hours from sign-in; the keeper
/// signs in again from the stored credentials before then, or at once on a 401.</para>
///
/// <para><b>History is metered.</b> IG allows 10,000 history points a week per account, and a chart that
/// re-read its forming bar every few seconds would spend them in a day. So history is capped per request
/// (<see cref="IgOptions.HistoryMaxPoints"/>) and the forming bar is built from the polled snapshots
/// instead. Snapshots carry no sizes and no depth is published.</para>
///
/// <para>Instruments are IG epics. Written 2026-09-25 from IG's REST API reference; not yet run against a
/// real account.</para>
/// </summary>
internal sealed class RealIgClient : KeptSessionClient<IgOptions>
{
    public RealIgClient(ILogger<RealIgClient> logger, IOptions<IgOptions> options, IBrokerCredentialSource credentials, IBrokerSessionStore store)
        : base(logger, options.Value, credentials, store, BrokerKind.IgGroup) { }

    public override BrokerKind Kind => BrokerKind.IgGroup;
    protected override string BrokerName => "IG";
    protected override string SecTypeOf(string symbol) => CurrencyPair(symbol) is null ? "CFD" : "CASH";
    protected override string ExchangeOf(string symbol) => "IG";

    /// <summary>Indices and commodities are quoted in USD unless the epic is a currency pair.</summary>
    protected override string CurrencyOf(string symbol) => CurrencyPair(symbol)?[3..] ?? "USD";

    /// <summary>The pair in a currency epic (<c>CS.D.EURUSD.CFD.IP</c> → <c>EURUSD</c>), or null. Gold is
    /// <c>CS.D.USCGC…</c>, five letters, and is not a pair.</summary>
    internal static string? CurrencyPair(string epic)
    {
        var parts = epic.Split('.');
        return parts.Length > 2 && parts[0] == "CS" && parts[2].Length == 6 && parts[2].All(char.IsAsciiLetterUpper) ? parts[2] : null;
    }

    private string Host => Credential.Extra.Trim().Equals("demo", StringComparison.OrdinalIgnoreCase) ? Options.DemoRestBaseUrl : Options.RestBaseUrl;

    /// <summary>Renewal is a new sign-in with the stored credentials.</summary>
    protected override Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct) =>
        IgSignIn.SessionAsync(Http, Host, app, DateTimeOffset.UtcNow, ct);

    protected override void Authorize(HttpRequestMessage request, KeptSession session)
    {
        request.Headers.TryAddWithoutValidation("X-IG-API-KEY", Credential.Key.Trim());
        request.Headers.TryAddWithoutValidation("CST", session.AccessToken);
        request.Headers.TryAddWithoutValidation("X-SECURITY-TOKEN", session.Secret);
        request.Headers.TryAddWithoutValidation("Accept", "application/json; charset=UTF-8");
    }

    private Task<(JsonDocument Doc, string Body)> GetVersionedAsync(string pathAndQuery, int version, CancellationToken ct) =>
        SendAsync(session =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, (session.Server.Length > 0 ? session.Server : Host) + pathAndQuery);
            request.Headers.TryAddWithoutValidation("Version", version.ToString(CultureInfo.InvariantCulture));
            return request;
        }, ct);

    protected override async Task CheckSessionAsync(CancellationToken ct)
    {
        var (doc, _) = await GetVersionedAsync("/session", 1, ct).ConfigureAwait(false);
        doc.Dispose();
    }

    // ── Snapshots ───────────────────────────────────────────────────────────────────────────────

    private async Task<PolledQuote?> FetchSnapshotAsync(string epic, CancellationToken ct)
    {
        var (doc, _) = await GetVersionedAsync($"/markets/{Uri.EscapeDataString(epic)}", 3, ct).ConfigureAwait(false);
        using (doc) return ParseSnapshot(doc.RootElement, DateTime.UtcNow);
    }

    /// <summary><c>{"snapshot":{"bid","offer","marketStatus","updateTime",…}}</c>. The update time is a
    /// wall-clock time without a date or zone, so the poll's own time is used.</summary>
    internal static PolledQuote? ParseSnapshot(JsonElement root, DateTime now)
    {
        if (!root.TryGetProperty("snapshot", out var s)) return null;
        var (bid, offer) = (CryptoConvert.D(s, "bid"), CryptoConvert.D(s, "offer"));
        return bid > 0 && offer > 0 ? new PolledQuote(now, bid, offer, 0, 0) : null;
    }

    private IAsyncEnumerable<PolledQuote> Snapshots(Contract contract, CancellationToken ct)
    {
        var epic = Symbol(contract);
        return Poll("prices", token => FetchSnapshotAsync(epic, token), ct);
    }

    public override async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var quote in Snapshots(contract, ct).ConfigureAwait(false))
            if (quote.ToTick() is { } tick) yield return tick;
    }

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default) =>
        throw new NotSupportedException("IG publishes a dealing price, not an order book.");

    public override IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, CancellationToken ct = default) =>
        QuoteBarStreams.BuildAsync(Snapshots(contract, ct), barSize, ct);

    // ── History ─────────────────────────────────────────────────────────────────────────────────

    protected override bool HasInterval(BarSize size) => true;

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, TimeSpan span, CancellationToken ct)
    {
        var points = Math.Clamp((int)Math.Ceiling(span / size.ToTimeSpan()) + 1, 1, Math.Max(1, Options.HistoryMaxPoints));
        var (doc, body) = await GetVersionedAsync(
            $"/prices/{Uri.EscapeDataString(symbol)}?resolution={Resolution(size)}&max={points}&pageSize=0", 3, ct).ConfigureAwait(false);
        using (doc)
        {
            if (Allowance(doc.RootElement) is { } left)
                Logger.LogInformation("IG history: {Points} points spent; {Left} left this week.", points, left);
            return WireFormat.OrWarn(ParsePrices(doc.RootElement), body, Logger, BrokerName, "prices");
        }
    }

    internal static string Resolution(BarSize size) => size switch
    {
        BarSize.OneMinute => "MINUTE",
        BarSize.ThreeMinutes => "MINUTE_3",
        BarSize.FiveMinutes => "MINUTE_5",
        BarSize.FifteenMinutes => "MINUTE_15",
        BarSize.OneHour => "HOUR",
        _ => "DAY",
    };

    internal static long? Allowance(JsonElement root) =>
        SignInProof.Text(root, "metadata.allowance.remainingAllowance") is { } left
        && long.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary><c>{"prices":[{"snapshotTimeUTC","openPrice":{"bid","ask","lastTraded"},"highPrice",
    /// "lowPrice","closePrice","lastTradedVolume"}]}</c>. Each price is the bid/ask mid.</summary>
    internal static IReadOnlyList<Bar> ParsePrices(JsonElement root)
    {
        var bars = new List<Bar>();
        if (!root.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Array) return bars;
        foreach (var row in prices.EnumerateArray())
        {
            var text = SignInProof.Text(row, "snapshotTimeUTC") ?? SignInProof.Text(row, "snapshotTime")?.Replace('/', '-');
            if (text is null || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time))
                continue;

            double Mid(string field) => row.TryGetProperty(field, out var p) && p.ValueKind == JsonValueKind.Object
                ? (CryptoConvert.D(p, "bid") + CryptoConvert.D(p, "ask")) / 2
                : 0;

            var bar = new Bar(time, Mid("openPrice"), Mid("highPrice"), Mid("lowPrice"), Mid("closePrice"), (long)CryptoConvert.D(row, "lastTradedVolume"));
            if (bar.Open > 0 && bar.Close > 0) bars.Add(bar);
        }

        return bars;
    }
}

/// <summary>
/// IG's sign-in: <c>POST /session</c> (version 2) with the API key header and the user name and password.
/// The session is in the response headers, not the body. Stored: key = API key, account = user name,
/// secret = password, extra = <c>live</c>/<c>demo</c>.
/// </summary>
internal sealed class IgSignIn : IBrokerSignIn
{
    /// <summary>IG's session tokens last six hours from sign-in; renewed a little before.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(5.5);

    private readonly IgOptions _options;

    public IgSignIn() : this(new IgOptions()) { }

    internal IgSignIn(IgOptions options) => _options = options;

    public BrokerKind Broker => BrokerKind.IgGroup;

    public SignInStyle Style => SignInStyle.Credentials;

    public Task<SessionIssue> SignInAsync(
        HttpClient http, BrokerCredential app, string proof, string redirectUri, DateTimeOffset now, CancellationToken ct)
    {
        var host = app.Extra.Trim().Equals("demo", StringComparison.OrdinalIgnoreCase) ? _options.DemoRestBaseUrl : _options.RestBaseUrl;
        return OAuthTokens.IssueAsync(async () => await SessionAsync(http, host, app, now, ct).ConfigureAwait(false), app.Account.Trim());
    }

    public static async Task<KeptSession> SessionAsync(HttpClient http, string host, BrokerCredential app, DateTimeOffset now, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{host}/session")
        {
            Content = new StringContent(
                new JsonObject { ["identifier"] = app.Account.Trim(), ["password"] = app.Secret }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-IG-API-KEY", app.Key.Trim());
        request.Headers.TryAddWithoutValidation("Version", "2");
        request.Headers.TryAddWithoutValidation("Accept", "application/json; charset=UTF-8");

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var cst = response.Headers.TryGetValues("CST", out var c) ? c.FirstOrDefault() : null;
        var security = response.Headers.TryGetValues("X-SECURITY-TOKEN", out var s) ? s.FirstOrDefault() : null;
        return Read((int)response.StatusCode, body, cst, security, host, now);
    }

    /// <summary>The session from the headers, and the account from <c>currentAccountId</c>; a refusal is
    /// <c>{"errorCode":"error.security.invalid-details"}</c>.</summary>
    internal static KeptSession Read(int status, string body, string? cst, string? security, string host, DateTimeOffset now)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(body.Length == 0 ? "{}" : body).RootElement.Clone(); }
        catch (JsonException) { root = JsonDocument.Parse("{}").RootElement.Clone(); }

        if (status is < 200 or >= 300 || string.IsNullOrWhiteSpace(cst) || string.IsNullOrWhiteSpace(security))
            throw new InvalidOperationException($"IG refused: {SignInProof.Text(root, "errorCode") ?? SignInProof.Snippet(body)} (HTTP {status})");

        return new KeptSession
        {
            AccessToken = cst,
            Secret = security,
            Server = host,
            Extra = SignInProof.Text(root, "currentAccountId") ?? string.Empty,
            ExpiresUtc = now + Lifetime,
        };
    }
}
