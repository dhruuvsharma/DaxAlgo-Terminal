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

namespace TradingTerminal.Infrastructure.Questrade;

/// <summary>
/// Questrade: Level 1 quotes polled and candles over its REST API.
///
/// <para><b>Session.</b> The user generates a refresh token in Questrade's API centre and pastes it. Every
/// exchange returns a 30-minute access token, the API server to send requests to, and a <b>new</b> refresh
/// token — the old one is dead from that moment, so the keeper writes each renewal back to the store at
/// once. A token left unused for a week expires and has to be generated again.</para>
///
/// <para><b>Instruments</b> are Questrade symbols, looked up once for the numeric id every market call takes.
/// Questrade's API publishes no depth and no per-trade prints. Real-time quotes need a market-data
/// subscription on the account; without one they are delayed, which the quote itself does not say.</para>
///
/// <para>Written 2026-09-25 from Questrade's API documentation; not yet run against a real account.</para>
/// </summary>
internal sealed class RealQuestradeClient : KeptSessionClient<QuestradeOptions>
{
    private readonly ConcurrentDictionary<string, long> _ids = new(StringComparer.OrdinalIgnoreCase);

    public RealQuestradeClient(
        ILogger<RealQuestradeClient> logger, IOptions<QuestradeOptions> options, IBrokerCredentialSource credentials, IBrokerSessionStore store)
        : base(logger, options.Value, credentials, store, BrokerKind.Questrade) { }

    public override BrokerKind Kind => BrokerKind.Questrade;
    protected override string BrokerName => "Questrade";
    protected override string SignInAdvice => "Paste a new refresh token from Questrade's API centre into the login window.";
    protected override string ExchangeOf(string symbol) => symbol.EndsWith(".TO", StringComparison.OrdinalIgnoreCase) ? "TSX" : "SMART";
    protected override string CurrencyOf(string symbol) =>
        symbol.EndsWith(".TO", StringComparison.OrdinalIgnoreCase) || symbol.EndsWith(".V", StringComparison.OrdinalIgnoreCase) ? "CAD" : "USD";

    protected override Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct) =>
        QuestradeSignIn.ExchangeAsync(Http, Options.AuthBaseUrl, session.RefreshToken, DateTimeOffset.UtcNow, ct);

    /// <summary>A URL on the API server this session was issued for.</summary>
    private Task<(JsonDocument Doc, string Body)> ApiGetAsync(string pathAndQuery, CancellationToken ct) =>
        SendAsync(session => new HttpRequestMessage(HttpMethod.Get, session.Server.TrimEnd('/') + pathAndQuery), ct);

    protected override async Task CheckSessionAsync(CancellationToken ct)
    {
        var (doc, _) = await ApiGetAsync("/v1/time", ct).ConfigureAwait(false);
        doc.Dispose();
    }

    private async Task<long> IdAsync(string symbol, CancellationToken ct)
    {
        if (_ids.TryGetValue(symbol, out var id)) return id;
        var (doc, body) = await ApiGetAsync($"/v1/symbols/search?prefix={Uri.EscapeDataString(symbol)}", ct).ConfigureAwait(false);
        using (doc)
        {
            id = PickSymbolId(doc.RootElement, symbol)
                ?? throw new InvalidOperationException($"Questrade found no symbol {symbol}: {(body.Length > 200 ? body[..200] : body)}");
        }

        return _ids[symbol] = id;
    }

    /// <summary><c>{"symbols":[{"symbol","symbolId","description","securityType","listingExchange"}]}</c> — the
    /// exact symbol only; a prefix search also returns every symbol that merely starts the same way.</summary>
    internal static long? PickSymbolId(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("symbols", out var symbols) || symbols.ValueKind != JsonValueKind.Array) return null;
        foreach (var s in symbols.EnumerateArray())
            if (string.Equals(SignInProof.Text(s, "symbol"), symbol, StringComparison.OrdinalIgnoreCase) && s.TryGetProperty("symbolId", out var id))
                return CryptoConvert.L(id);
        return null;
    }

    // ── Quotes ──────────────────────────────────────────────────────────────────────────────────

    public override IAsyncEnumerable<Tick> SubscribeTicksAsync(Contract contract, CancellationToken ct = default)
    {
        var symbol = Symbol(contract);
        return Poll("quotes", async token =>
        {
            var id = await IdAsync(symbol, token).ConfigureAwait(false);
            var (doc, _) = await ApiGetAsync($"/v1/markets/quotes/{id}", token).ConfigureAwait(false);
            using (doc) return ParseQuote(doc.RootElement, Options.SizeScale);
        }, ct);
    }

    /// <summary><c>{"quotes":[{"symbol","bidPrice","bidSize","askPrice","askSize","lastTradeTime",…}]}</c>.</summary>
    internal static Tick? ParseQuote(JsonElement root, double scale)
    {
        if (!root.TryGetProperty("quotes", out var quotes) || quotes.ValueKind != JsonValueKind.Array || quotes.GetArrayLength() == 0) return null;
        var q = quotes[0];
        var (bid, ask) = (CryptoConvert.D(q, "bidPrice"), CryptoConvert.D(q, "askPrice"));
        if (bid <= 0 || ask <= 0) return null;
        return new Tick(DateTime.UtcNow, bid, ask,
            (long)Math.Round(CryptoConvert.D(q, "bidSize") * scale), (long)Math.Round(CryptoConvert.D(q, "askSize") * scale));
    }

    public override IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(Contract contract, int levels = 10, CancellationToken ct = default) =>
        throw new NotSupportedException("Questrade's API publishes Level 1 only — no order-book depth.");

    // ── Candles ─────────────────────────────────────────────────────────────────────────────────

    protected override bool HasInterval(BarSize size) => true;

    protected override async Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, TimeSpan span, CancellationToken ct)
    {
        var id = await IdAsync(symbol, ct).ConfigureAwait(false);
        var end = DateTimeOffset.UtcNow;
        // At most 2,000 candles a request.
        var reach = TimeSpan.FromTicks(Math.Min(Math.Max(span.Ticks, TimeSpan.FromDays(size == BarSize.OneDay ? 30 : 1).Ticks), size.ToTimeSpan().Ticks * 2000));
        var start = end - reach;
        var (doc, body) = await ApiGetAsync(
            $"/v1/markets/candles/{id}?startTime={Uri.EscapeDataString(Iso(start))}&endTime={Uri.EscapeDataString(Iso(end))}&interval={Interval(size)}", ct)
            .ConfigureAwait(false);
        using (doc) return WireFormat.OrWarn(ParseCandles(doc.RootElement), body, Logger, BrokerName, "candles");
    }

    private static string Iso(DateTimeOffset time) => time.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);

    internal static string Interval(BarSize size) => size switch
    {
        BarSize.OneMinute => "OneMinute",
        BarSize.ThreeMinutes => "ThreeMinutes",
        BarSize.FiveMinutes => "FiveMinutes",
        BarSize.FifteenMinutes => "FifteenMinutes",
        BarSize.OneHour => "OneHour",
        _ => "OneDay",
    };

    /// <summary><c>{"candles":[{"start":"2014-01-02T00:00:00.000000-05:00","end","low","high","open","close","volume"}]}</c>.</summary>
    internal static IReadOnlyList<Bar> ParseCandles(JsonElement root)
    {
        var bars = new List<Bar>();
        if (!root.TryGetProperty("candles", out var candles) || candles.ValueKind != JsonValueKind.Array) return bars;
        foreach (var c in candles.EnumerateArray())
            if (SignInProof.Text(c, "start") is { } start && DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
                bars.Add(new Bar(time.UtcDateTime,
                    CryptoConvert.D(c, "open"), CryptoConvert.D(c, "high"), CryptoConvert.D(c, "low"), CryptoConvert.D(c, "close"),
                    (long)CryptoConvert.D(c, "volume")));
        return bars;
    }
}

/// <summary>
/// Questrade's sign-in: the refresh token pasted from its API centre is exchanged at
/// <c>/oauth2/token?grant_type=refresh_token</c>. The answer names the API server and a new refresh token;
/// the pasted one is spent.
/// </summary>
internal sealed class QuestradeSignIn : IBrokerSignIn
{
    private readonly string _authBase;

    public QuestradeSignIn() : this(new QuestradeOptions().AuthBaseUrl) { }

    internal QuestradeSignIn(string authBase) => _authBase = authBase;

    public BrokerKind Broker => BrokerKind.Questrade;

    public SignInStyle Style => SignInStyle.Token;

    public Task<SessionIssue> SignInAsync(
        HttpClient http, BrokerCredential app, string proof, string redirectUri, DateTimeOffset now, CancellationToken ct)
    {
        var token = proof.Trim();
        if (token.Length == 0) return Task.FromResult(SessionIssue.Refused("Paste the refresh token from Questrade's API centre."));
        return OAuthTokens.IssueAsync(() => ExchangeAsync(http, _authBase, token, now, ct));
    }

    public static async Task<KeptSession> ExchangeAsync(HttpClient http, string authBase, string refreshToken, DateTimeOffset now, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{authBase}/oauth2/token?grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refreshToken)}");
        var (status, root, body) = await SignInProof.SendAsync(http, request, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300)
            throw new InvalidOperationException(
                $"Questrade refused the refresh token (HTTP {status}) — generate a new one in its API centre. {OAuthTokens.Refusal(root) ?? SignInProof.Snippet(body)}");

        var session = OAuthTokens.Read(root, now, null, "Questrade");
        var server = SignInProof.Text(root, "api_server");
        if (string.IsNullOrWhiteSpace(server)) throw new InvalidOperationException("Questrade named no API server.");
        return session with { Server = server };
    }
}
