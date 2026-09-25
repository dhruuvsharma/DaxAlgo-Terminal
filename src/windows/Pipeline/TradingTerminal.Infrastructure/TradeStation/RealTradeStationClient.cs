using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.TradeStation;

/// <summary>
/// TradeStation v3: history over REST, and quotes, the aggregated book and live bars over its HTTP
/// streams — responses that never end, carrying one JSON object after another.
///
/// <para><b>Session.</b> OAuth 2 through TradeStation's Auth0 tenant: a browser sign-in returns a code,
/// exchanged for a 20-minute access token and a refresh token that the keeper spends to renew it. The
/// market-data, read-account and trade scopes are requested — trade for the order route.</para>
///
/// <para><b>Streams.</b> A quote stream sends the whole quote first and then only the fields that changed,
/// so quotes are merged; it also sends heartbeats, and a <c>GoAway</c> that asks the client to reconnect.
/// Bars carry the time they <i>close</i>, so the bucket start is that time less one bar.</para>
///
/// <para>Paths and fields are from TradeStation's v3 API reference. Written 2026-09-25; not yet run
/// against a real account.</para>
/// </summary>
internal sealed class RealTradeStationClient : KeptSessionClient<TradeStationOptions>
{
    private const string StreamType = "application/vnd.tradestation.streams.v2+json";

    public RealTradeStationClient(
        ILogger<RealTradeStationClient> logger, IOptions<TradeStationOptions> options, IBrokerCredentialSource credentials, IBrokerSessionStore store)
        : base(logger, options.Value, credentials, store, BrokerKind.TradeStation) { }

    public override BrokerKind Kind => BrokerKind.TradeStation;
    protected override string BrokerName => "TradeStation";
    protected override string CurrencyOf(string symbol) => "USD";
    protected override string SecTypeOf(string symbol) => symbol.StartsWith('@') ? "FUT" : "STK";
    protected override string ExchangeOf(string symbol) => "SMART";

    protected override Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct) =>
        TradeStationSignIn.TokenAsync(Http, Options.AuthBaseUrl,
            [new("grant_type", "refresh_token"), new("client_id", app.Key.Trim()), new("client_secret", app.Secret.Trim()),
             new("refresh_token", session.RefreshToken)], DateTimeOffset.UtcNow, session, ct);

    protected override async Task CheckSessionAsync(CancellationToken ct)
    {
        var (doc, _) = await GetAsync($"{Options.RestBaseUrl}/v3/marketdata/quotes/SPY", ct).ConfigureAwait(false);
        doc.Dispose();
    }

    // ── History ─────────────────────────────────────────────────────────────────────────────────

    protected override bool HasInterval(BarSize size) => true;

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, TimeSpan span, CancellationToken ct)
    {
        var step = size.ToTimeSpan();
        var count = Math.Clamp((int)Math.Ceiling(span / step) + 1, 1, 57_600);
        var url = $"{Options.RestBaseUrl}/v3/marketdata/barcharts/{Uri.EscapeDataString(symbol)}?{Interval(size)}&barsback={count}";
        var (doc, body) = await GetAsync(url, ct).ConfigureAwait(false);
        using (doc) return WireFormat.OrWarn(ParseBars(doc.RootElement, step), body, Logger, BrokerName, "bars");
    }

    internal static string Interval(BarSize size) => size == BarSize.OneDay
        ? "interval=1&unit=Daily"
        : $"interval={(int)size.ToTimeSpan().TotalMinutes}&unit=Minute";

    /// <summary><c>{"Bars":[{"Open","High","Low","Close","TimeStamp","TotalVolume",…}]}</c>, numbers as strings.</summary>
    internal static IReadOnlyList<Bar> ParseBars(JsonElement root, TimeSpan step)
    {
        var bars = new List<Bar>();
        if (!root.TryGetProperty("Bars", out var rows) || rows.ValueKind != JsonValueKind.Array) return bars;
        foreach (var row in rows.EnumerateArray())
            if (ParseBar(row, step) is { } bar) bars.Add(bar);
        return bars;
    }

    /// <summary>One bar. Its <c>TimeStamp</c> is when it closes: an intraday bar starts one step earlier, a
    /// daily bar on its own date.</summary>
    internal static Bar? ParseBar(JsonElement row, TimeSpan step)
    {
        if (SignInProof.Text(row, "TimeStamp") is not { } stamp
            || !DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var closes))
            return null;

        var start = step >= TimeSpan.FromDays(1)
            ? BarRollup.BucketStart(closes.UtcDateTime, step)
            : closes.UtcDateTime - step;
        return new Bar(start,
            CryptoConvert.D(row, "Open"), CryptoConvert.D(row, "High"), CryptoConvert.D(row, "Low"), CryptoConvert.D(row, "Close"),
            (long)CryptoConvert.D(row, "TotalVolume"));
    }

    public override async IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var step = barSize.ToTimeSpan();
        var url = $"{Options.RestBaseUrl}/v3/marketdata/stream/barcharts/{Uri.EscapeDataString(Symbol(contract))}?{Interval(barSize)}&barsback=1";
        await foreach (var text in StreamObjectsAsync(url, "bars", StreamType, ct).ConfigureAwait(false))
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("TimeStamp", out _) && ParseBar(doc.RootElement, step) is { } bar) yield return bar;
        }
    }

    // ── Streams ─────────────────────────────────────────────────────────────────────────────────

    public override async IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"{Options.RestBaseUrl}/v3/marketdata/stream/quotes/{Uri.EscapeDataString(Symbol(contract))}";
        var quote = new QuoteState();
        await foreach (var text in StreamObjectsAsync(url, "quotes", StreamType, ct).ConfigureAwait(false))
        {
            using var doc = JsonDocument.Parse(text);
            if (quote.Apply(doc.RootElement, Options.SizeScale, Logger) is { } tick) yield return tick;
        }
    }

    public override async IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract, int levels = 10, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"{Options.RestBaseUrl}/v3/marketdata/stream/marketdepth/aggregates/{Uri.EscapeDataString(Symbol(contract))}?maxlevels={Math.Clamp(levels, 1, 50)}";
        await foreach (var text in StreamObjectsAsync(url, "depth", StreamType, ct).ConfigureAwait(false))
        {
            using var doc = JsonDocument.Parse(text);
            if (ParseDepth(doc.RootElement, Options.SizeScale) is { } book) yield return book;
        }
    }

    /// <summary><c>{"Bids":[{"Price","TotalSize","LatestTime",…}],"Asks":[…]}</c>, the whole book each time.</summary>
    internal static DepthSnapshot? ParseDepth(JsonElement root, double scale)
    {
        if (!root.TryGetProperty("Bids", out _) && !root.TryGetProperty("Asks", out _)) return null;
        var latest = DateTime.MinValue;
        var bids = Levels(root, "Bids", scale, ref latest);
        var asks = Levels(root, "Asks", scale, ref latest);
        return new DepthSnapshot(latest == DateTime.MinValue ? DateTime.UtcNow : latest, bids, asks);
    }

    private static List<DepthLevel> Levels(JsonElement root, string side, double scale, ref DateTime latest)
    {
        var levels = new List<DepthLevel>();
        if (!root.TryGetProperty(side, out var rows) || rows.ValueKind != JsonValueKind.Array) return levels;
        foreach (var row in rows.EnumerateArray())
        {
            var price = CryptoConvert.D(row, "Price");
            var size = CryptoConvert.D(row, "TotalSize");
            if (price > 0 && size > 0) levels.Add(new DepthLevel(price, (long)Math.Round(size * scale)));
            if (SignInProof.Text(row, "LatestTime") is { } t && DateTime.TryParse(t, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time) && time > latest)
                latest = time;
        }

        return levels;
    }

    /// <summary>A quote merged from a stream that sends only what changed.</summary>
    internal sealed class QuoteState
    {
        private double _bid, _ask, _bidSize, _askSize;

        public Tick? Apply(JsonElement update, double scale, ILogger? logger = null)
        {
            if (update.TryGetProperty("Error", out var error))
            {
                logger?.LogWarning("TradeStation quote stream: {Error} {Message}", error.GetRawText(), SignInProof.Text(update, "Message"));
                return null;
            }

            // Heartbeats and stream status notices carry no quote.
            if (!update.TryGetProperty("Symbol", out _)) return null;

            if (update.TryGetProperty("Bid", out var bid)) _bid = CryptoConvert.D(bid);
            if (update.TryGetProperty("Ask", out var ask)) _ask = CryptoConvert.D(ask);
            if (update.TryGetProperty("BidSize", out var bidSize)) _bidSize = CryptoConvert.D(bidSize);
            if (update.TryGetProperty("AskSize", out var askSize)) _askSize = CryptoConvert.D(askSize);
            if (_bid <= 0 || _ask <= 0) return null;

            var time = SignInProof.Text(update, "TradeTime") is { } t
                && DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                && DateTime.UtcNow - parsed < TimeSpan.FromMinutes(1)
                    ? parsed
                    : DateTime.UtcNow;
            return new Tick(time, _bid, _ask, (long)Math.Round(_bidSize * scale), (long)Math.Round(_askSize * scale));
        }
    }
}

/// <summary>
/// TradeStation's OAuth 2 sign-in (Auth0): the authorize page redirects back with <c>?code=…</c>, exchanged
/// at <c>/oauth/token</c> with the client id and secret in the form. <c>offline_access</c> is what makes it
/// issue a refresh token.
/// </summary>
internal sealed class TradeStationSignIn : IBrokerSignIn
{
    private readonly TradeStationOptions _options;

    public TradeStationSignIn() : this(new TradeStationOptions()) { }

    internal TradeStationSignIn(TradeStationOptions options) => _options = options;

    public BrokerKind Broker => BrokerKind.TradeStation;

    public SignInStyle Style => SignInStyle.Browser;

    public string? SignInUrl(BrokerCredential app, string redirectUri) =>
        $"{_options.AuthBaseUrl}/authorize?response_type=code&client_id={Uri.EscapeDataString(app.Key.Trim())}"
        + $"&audience={Uri.EscapeDataString(_options.RestBaseUrl)}&redirect_uri={Uri.EscapeDataString(redirectUri)}"
        + $"&scope={Uri.EscapeDataString(_options.Scopes)}&state=daxalgo";

    public Task<SessionIssue> SignInAsync(
        HttpClient http, BrokerCredential app, string proof, string redirectUri, DateTimeOffset now, CancellationToken ct)
    {
        var code = SignInProof.Parameter(proof, "code");
        if (string.IsNullOrWhiteSpace(code)) return Task.FromResult(SessionIssue.Refused("Paste the address TradeStation redirected to — it carries the code."));

        return OAuthTokens.IssueAsync(() => TokenAsync(http, _options.AuthBaseUrl,
            [new("grant_type", "authorization_code"), new("client_id", app.Key.Trim()), new("client_secret", app.Secret.Trim()),
             new("code", code), new("redirect_uri", redirectUri)], now, null, ct));
    }

    public static Task<KeptSession> TokenAsync(
        HttpClient http, string authBase, KeyValuePair<string, string>[] form, DateTimeOffset now, KeptSession? previous, CancellationToken ct) =>
        OAuthTokens.PostFormAsync(http, $"{authBase}/oauth/token", form, now, previous, "TradeStation", ct);
}
