using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Saxo;

/// <summary>
/// Saxo Bank OpenAPI: prices and depth polled from <c>/trade/v1/infoprices</c>, history from
/// <c>/chart/v3/charts</c>.
///
/// <para><b>Session.</b> OAuth 2 code flow. Saxo's refresh tokens are single-use and last an hour: every
/// refresh returns a new one and kills the old, so the renewed session is written straight back to the
/// store. A terminal closed for more than an hour needs a new sign-in.</para>
///
/// <para><b>Instruments</b> are written <c>ASSETTYPE:SYMBOL</c>; the symbol is looked up in
/// <c>/ref/v1/instruments</c> for Saxo's numeric id (the Uic), or given as the Uic directly.</para>
///
/// <para><b>Why polled.</b> Saxo streams through subscriptions delivered over a WebSocket in its own binary
/// envelope; the REST prices are documented and simple, and at two seconds stay inside the 120-a-minute
/// limit. FX and CFD charts carry bid and ask bars — the mid is used.</para>
///
/// <para>Written 2026-09-25 from Saxo's developer portal; not yet run against a real account.</para>
/// </summary>
internal sealed class RealSaxoClient : KeptSessionClient<SaxoOptions>
{
    private readonly ConcurrentDictionary<string, (long Uic, string AssetType)> _instruments = new(StringComparer.OrdinalIgnoreCase);

    public RealSaxoClient(ILogger<RealSaxoClient> logger, IOptions<SaxoOptions> options, IBrokerCredentialSource credentials, IBrokerSessionStore store)
        : base(logger, options.Value, credentials, store, BrokerKind.SaxoBank) { }

    public override BrokerKind Kind => BrokerKind.SaxoBank;
    protected override string BrokerName => "Saxo";
    protected override string SignInAdvice => "Sign in to Saxo in the login window — a Saxo sign-in lapses after an hour without the terminal running.";

    protected override string SecTypeOf(string symbol) => AssetTypeOf(symbol) switch
    {
        "FxSpot" or "FxForwards" => "CASH",
        "Stock" or "Etf" => "STK",
        "ContractFutures" => "FUT",
        _ => "CFD",
    };

    protected override string ExchangeOf(string symbol) => "SAXO";

    protected override string CurrencyOf(string symbol)
    {
        var (asset, name) = Split(symbol);
        return asset == "FxSpot" && name.Length == 6 ? name[3..].ToUpperInvariant() : "USD";
    }

    internal static string AssetTypeOf(string symbol) => Split(symbol).AssetType;

    /// <summary><c>FxSpot:EURUSD</c> → (<c>FxSpot</c>, <c>EURUSD</c>); <c>Stock:AAPL:xnas</c> keeps the
    /// exchange suffix, which is part of Saxo's symbol.</summary>
    internal static (string AssetType, string Symbol) Split(string symbol)
    {
        var colon = symbol.IndexOf(':');
        return colon < 0 ? ("FxSpot", symbol) : (symbol[..colon], symbol[(colon + 1)..]);
    }

    protected override Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct) =>
        SaxoSignIn.TokenAsync(Http, Options.AuthBaseUrl, app,
            [new("grant_type", "refresh_token"), new("refresh_token", session.RefreshToken), new("redirect_uri", session.Extra)],
            DateTimeOffset.UtcNow, session, ct);

    protected override async Task CheckSessionAsync(CancellationToken ct)
    {
        var (doc, _) = await GetAsync($"{Options.RestBaseUrl}/port/v1/users/me", ct).ConfigureAwait(false);
        doc.Dispose();
    }

    /// <summary>The Uic and asset type for an instrument entry.</summary>
    private async Task<(long Uic, string AssetType)> ResolveAsync(string symbol, CancellationToken ct)
    {
        if (_instruments.TryGetValue(symbol, out var known)) return known;
        var (asset, name) = Split(symbol);
        if (long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uic)) return _instruments[symbol] = (uic, asset);

        var keyword = name.Contains(':') ? name[..name.IndexOf(':')] : name;
        var (doc, body) = await GetAsync(
            $"{Options.RestBaseUrl}/ref/v1/instruments?Keywords={Uri.EscapeDataString(keyword)}&AssetTypes={Uri.EscapeDataString(asset)}", ct).ConfigureAwait(false);
        using (doc)
        {
            uic = PickUic(doc.RootElement, name)
                ?? throw new InvalidOperationException($"Saxo found no {asset} instrument {name}: {(body.Length > 200 ? body[..200] : body)}");
        }

        return _instruments[symbol] = (uic, asset);
    }

    /// <summary><c>{"Data":[{"Identifier","Symbol","AssetType","Description"}]}</c> — the exact symbol when
    /// it is there, else the first match.</summary>
    internal static long? PickUic(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0) return null;
        foreach (var row in data.EnumerateArray())
            if (string.Equals(SignInProof.Text(row, "Symbol"), symbol, StringComparison.OrdinalIgnoreCase))
                return CryptoConvert.L(row.GetProperty("Identifier"));
        return data[0].TryGetProperty("Identifier", out var first) ? CryptoConvert.L(first) : null;
    }

    // ── Prices ──────────────────────────────────────────────────────────────────────────────────

    private async Task<JsonDocument> InfoPriceAsync(string symbol, string groups, CancellationToken ct)
    {
        var (uic, asset) = await ResolveAsync(symbol, ct).ConfigureAwait(false);
        var (doc, _) = await GetAsync($"{Options.RestBaseUrl}/trade/v1/infoprices?Uic={uic}&AssetType={asset}&FieldGroups={groups}", ct).ConfigureAwait(false);
        return doc;
    }

    public override IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default)
    {
        var symbol = Symbol(contract);
        return Poll("prices", async token =>
        {
            using var doc = await InfoPriceAsync(symbol, "Quote", token).ConfigureAwait(false);
            return ParseQuote(doc.RootElement, Options.SizeScale);
        }, ct);
    }

    /// <summary><c>{"Quote":{"Bid","Ask","BidSize","AskSize",…},"LastUpdated":"…Z"}</c>.</summary>
    internal static Tick? ParseQuote(JsonElement root, double scale)
    {
        if (!root.TryGetProperty("Quote", out var q)) return null;
        var (bid, ask) = (CryptoConvert.D(q, "Bid"), CryptoConvert.D(q, "Ask"));
        if (bid <= 0 || ask <= 0) return null;
        return new Tick(Updated(root), bid, ask,
            (long)Math.Round(CryptoConvert.D(q, "BidSize") * scale), (long)Math.Round(CryptoConvert.D(q, "AskSize") * scale));
    }

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default)
    {
        var symbol = Symbol(contract);
        return Poll("depth", async token =>
        {
            using var doc = await InfoPriceAsync(symbol, "MarketDepth", token).ConfigureAwait(false);
            return ParseDepth(doc.RootElement, Options.SizeScale) is { } d
                ? d with { Bids = [.. d.Bids.Take(levels)], Asks = [.. d.Asks.Take(levels)] }
                : null;
        }, ct);
    }

    /// <summary><c>{"MarketDepth":{"Bid":[…],"BidSize":[…],"Ask":[…],"AskSize":[…],"NoOfBids","NoOfOffers"}}</c> —
    /// prices and sizes in parallel arrays.</summary>
    internal static DepthSnapshot? ParseDepth(JsonElement root, double scale)
    {
        if (!root.TryGetProperty("MarketDepth", out var depth)) return null;
        var bids = Side(depth, "Bid", "BidSize", scale);
        var asks = Side(depth, "Ask", "AskSize", scale);
        return bids.Count == 0 && asks.Count == 0 ? null : new DepthSnapshot(Updated(root), bids, asks);
    }

    private static List<DepthLevel> Side(JsonElement depth, string prices, string sizes, double scale)
    {
        var levels = new List<DepthLevel>();
        if (!depth.TryGetProperty(prices, out var p) || p.ValueKind != JsonValueKind.Array) return levels;
        var s = depth.TryGetProperty(sizes, out var sz) && sz.ValueKind == JsonValueKind.Array ? sz : default;
        for (var i = 0; i < p.GetArrayLength(); i++)
        {
            var price = CryptoConvert.D(p[i]);
            var size = s.ValueKind == JsonValueKind.Array && i < s.GetArrayLength() ? CryptoConvert.D(s[i]) : 0;
            if (price > 0) levels.Add(new DepthLevel(price, (long)Math.Round(size * scale)));
        }

        return levels;
    }

    private static DateTime Updated(JsonElement root) =>
        SignInProof.Text(root, "LastUpdated") is { } t
        && DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : DateTime.UtcNow;

    // ── History ─────────────────────────────────────────────────────────────────────────────────

    protected override bool HasInterval(BarSize size) => size != BarSize.ThreeMinutes;

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, TimeSpan span, CancellationToken ct)
    {
        var (uic, asset) = await ResolveAsync(symbol, ct).ConfigureAwait(false);
        var horizon = size == BarSize.OneDay ? 1440 : (int)size.ToTimeSpan().TotalMinutes;
        var count = Math.Clamp((int)Math.Ceiling(span / size.ToTimeSpan()) + 1, 1, 1200);
        var url = $"{Options.RestBaseUrl}/chart/v3/charts?Uic={uic}&AssetType={asset}&Horizon={horizon}&Count={count}"
            + $"&Mode=UpTo&Time={Uri.EscapeDataString(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}";
        var (doc, body) = await GetAsync(url, ct).ConfigureAwait(false);
        using (doc) return WireFormat.OrWarn(ParseChart(doc.RootElement), body, Logger, BrokerName, "chart");
    }

    /// <summary>
    /// <c>{"Data":[{"Time","Open","High","Low","Close","Volume"}]}</c> for stocks and futures, or bid and ask
    /// bars (<c>OpenBid</c>, <c>OpenAsk</c>, …) for FX and CFDs, which are averaged to the mid.
    /// </summary>
    internal static IReadOnlyList<Bar> ParseChart(JsonElement root)
    {
        var bars = new List<Bar>();
        if (!root.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Array) return bars;
        foreach (var row in data.EnumerateArray())
        {
            if (SignInProof.Text(row, "Time") is not { } t
                || !DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time))
                continue;

            double Price(string field) => row.TryGetProperty(field, out _)
                ? CryptoConvert.D(row, field)
                : (CryptoConvert.D(row, field + "Bid") + CryptoConvert.D(row, field + "Ask")) / 2;

            var bar = new Bar(time, Price("Open"), Price("High"), Price("Low"), Price("Close"), (long)CryptoConvert.D(row, "Volume"));
            if (bar.Open > 0 && bar.Close > 0) bars.Add(bar);
        }

        return bars;
    }
}

/// <summary>
/// Saxo's OAuth 2 sign-in: the authorize page redirects back with <c>?code=…</c>, exchanged at <c>/token</c>
/// with the app key and secret in the form. The redirect address is kept in the session, because every
/// refresh must send it again.
/// </summary>
internal sealed class SaxoSignIn : IBrokerSignIn
{
    private readonly string _authBase;

    public SaxoSignIn() : this(new SaxoOptions().AuthBaseUrl) { }

    internal SaxoSignIn(string authBase) => _authBase = authBase;

    public BrokerKind Broker => BrokerKind.SaxoBank;

    public SignInStyle Style => SignInStyle.Browser;

    public string? SignInUrl(BrokerCredential app, string redirectUri) =>
        $"{_authBase}/authorize?response_type=code&client_id={Uri.EscapeDataString(app.Key.Trim())}"
        + $"&state=daxalgo&redirect_uri={Uri.EscapeDataString(redirectUri)}";

    public Task<SessionIssue> SignInAsync(
        HttpClient http, BrokerCredential app, string proof, string redirectUri, DateTimeOffset now, CancellationToken ct)
    {
        var code = SignInProof.Parameter(proof, "code");
        if (string.IsNullOrWhiteSpace(code)) return Task.FromResult(SessionIssue.Refused("Paste the address Saxo redirected to — it carries the code."));

        return OAuthTokens.IssueAsync(() => TokenAsync(http, _authBase, app,
            [new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", redirectUri)],
            now, new KeptSession { Extra = redirectUri }, ct));
    }

    public static Task<KeptSession> TokenAsync(
        HttpClient http, string authBase, BrokerCredential app, KeyValuePair<string, string>[] form, DateTimeOffset now,
        KeptSession? previous, CancellationToken ct) =>
        OAuthTokens.PostFormAsync(http, $"{authBase}/token",
            [.. form, new("client_id", app.Key.Trim()), new("client_secret", app.Secret.Trim())], now, previous, "Saxo", ct);
}
