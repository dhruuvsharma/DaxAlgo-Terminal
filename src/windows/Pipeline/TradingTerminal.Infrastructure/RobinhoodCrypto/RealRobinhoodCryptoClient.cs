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
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.RobinhoodCrypto;

/// <summary>
/// Robinhood's Crypto Trading API: best bid and ask, polled.
///
/// <para><b>Every request is signed</b> with the user's Ed25519 private key over
/// <c>api_key + timestamp + path + method + body</c>, the timestamp in Unix seconds and the path with its
/// query. There is no session to renew; "signing in" is one signed call to the accounts endpoint, which
/// proves the key pair before the row will connect.</para>
///
/// <para><b>What it publishes is thin.</b> Best bid and ask with Robinhood's spread folded in, no sizes, no
/// history, no depth, no trades. Charts start empty and build their forming bar from the quotes. It is here
/// because a Robinhood crypto account is common, not because the feed is rich.</para>
///
/// <para>Written 2026-09-25 from Robinhood's Crypto Trading API documentation; not yet run against a real
/// account.</para>
/// </summary>
internal sealed class RealRobinhoodCryptoClient : RestBrokerClient<RobinhoodCryptoOptions>
{
    private readonly TimeProvider _time;
    private int _warnedHistory;

    public RealRobinhoodCryptoClient(
        ILogger<RealRobinhoodCryptoClient> logger, IOptions<RobinhoodCryptoOptions> options, IBrokerCredentialSource credentials,
        TimeProvider? time = null)
        : base(logger, options.Value, credentials)
    {
        _time = time ?? TimeProvider.System;
    }

    public override BrokerKind Kind => BrokerKind.RobinhoodCrypto;
    protected override string BrokerName => "Robinhood";
    protected override string SignInAdvice => "Sign in to Robinhood in the login window with your API key and private key.";
    protected override string SecTypeOf(string symbol) => "CRYPTO";
    protected override string ExchangeOf(string symbol) => "ROBINHOOD";
    protected override string CurrencyOf(string symbol) => symbol.Contains('-') ? symbol[(symbol.LastIndexOf('-') + 1)..] : "USD";

    private HttpRequestMessage Get(string pathAndQuery) =>
        RobinhoodSigning.Signed(HttpMethod.Get, Options.RestBaseUrl, pathAndQuery, "", Credential, _time.GetUtcNow());

    protected override async Task CheckSessionAsync(CancellationToken ct)
    {
        var (doc, _) = await SendJsonAsync(() => Get("/api/v1/crypto/trading/accounts/"), ct).ConfigureAwait(false);
        doc.Dispose();
    }

    private async Task<PolledQuote?> FetchQuoteAsync(string symbol, CancellationToken ct)
    {
        var (doc, _) = await SendJsonAsync(() => Get($"/api/v1/crypto/marketdata/best_bid_ask/?symbol={Uri.EscapeDataString(symbol)}"), ct).ConfigureAwait(false);
        using (doc) return ParseBestBidAsk(doc.RootElement);
    }

    /// <summary><c>{"results":[{"symbol","price","bid_inclusive_of_sell_spread","ask_inclusive_of_buy_spread",
    /// "timestamp"}]}</c>. The bid and ask are what Robinhood would actually fill at — spread included.</summary>
    internal static PolledQuote? ParseBestBidAsk(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0) return null;
        var r = results[0];
        var bid = CryptoConvert.D(r, "bid_inclusive_of_sell_spread");
        var ask = CryptoConvert.D(r, "ask_inclusive_of_buy_spread");
        if (bid <= 0 || ask <= 0) return null;
        var time = SignInProof.Text(r, "timestamp") is { } t
            && DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTime.UtcNow;
        return new PolledQuote(time, bid, ask, 0, 0, Last: CryptoConvert.D(r, "price"));
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
        throw new NotSupportedException("Robinhood's crypto API publishes a best bid and ask only — no order-book depth.");

    protected override bool HasInterval(BarSize size) => true;

    protected override Task<IReadOnlyList<Bar>> FetchBarsAsync(string symbol, BarSize size, TimeSpan span, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _warnedHistory, 1) == 0)
            Logger.LogInformation("Robinhood's crypto API has no bar history; charts build bars from quotes.");
        return Task.FromResult<IReadOnlyList<Bar>>([]);
    }

    public override IAsyncEnumerable<Bar> SubscribeBarsAsync(Contract contract, BarSize barSize, CancellationToken ct = default) =>
        QuoteBarStreams.BuildAsync(Quotes(contract, ct), barSize, ct);
}

/// <summary>Robinhood's request signature.</summary>
internal static class RobinhoodSigning
{
    /// <summary>The message signed: <c>api_key + timestamp + path + method + body</c>.</summary>
    public static string Message(string apiKey, long timestamp, string pathAndQuery, string method, string body) =>
        $"{apiKey}{timestamp.ToString(CultureInfo.InvariantCulture)}{pathAndQuery}{method}{body}";

    /// <summary>The base64 Ed25519 signature of the message, under the base64 private key.</summary>
    public static string Signature(string privateKeyBase64, string message) =>
        Convert.ToBase64String(Ed25519.Sign(Ed25519.SeedFrom(Convert.FromBase64String(privateKeyBase64.Trim())), Encoding.UTF8.GetBytes(message)));

    /// <summary>A signed request. Stored: key = API key, secret = the base64 private key.</summary>
    public static HttpRequestMessage Signed(HttpMethod method, string host, string pathAndQuery, string body, BrokerCredential app, DateTimeOffset now)
    {
        var timestamp = now.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(method, host + pathAndQuery);
        if (body.Length > 0) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("x-api-key", app.Key.Trim());
        request.Headers.TryAddWithoutValidation("x-timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("x-signature",
            Signature(app.Secret, Message(app.Key.Trim(), timestamp, pathAndQuery, method.Method, body)));
        return request;
    }
}

/// <summary>
/// Robinhood's "sign-in": one signed call to the accounts endpoint. A key pair Robinhood refuses is caught
/// here, at the login window, instead of as an empty chart later. The session stored is the account number.
/// </summary>
internal sealed class RobinhoodCryptoSignIn : IBrokerSignIn
{
    private readonly string _host;

    public RobinhoodCryptoSignIn() : this(new RobinhoodCryptoOptions().RestBaseUrl) { }

    internal RobinhoodCryptoSignIn(string host) => _host = host;

    public BrokerKind Broker => BrokerKind.RobinhoodCrypto;

    public SignInStyle Style => SignInStyle.Credentials;

    public async Task<SessionIssue> SignInAsync(
        HttpClient http, BrokerCredential app, string proof, string redirectUri, DateTimeOffset now, CancellationToken ct)
    {
        HttpRequestMessage request;
        try
        {
            request = RobinhoodSigning.Signed(HttpMethod.Get, _host, "/api/v1/crypto/trading/accounts/", "", app, now);
        }
        catch (FormatException ex)
        {
            return SessionIssue.Refused("That private key is not a base64 Ed25519 key: " + ex.Message);
        }

        using (request)
        {
            var (status, root, body) = await SignInProof.SendAsync(http, request, ct).ConfigureAwait(false);
            if (status is < 200 or >= 300)
                return SignInProof.Refusal(status, body, SignInProof.Text(root, "detail"), SignInProof.Text(root, "message"));

            var account = SignInProof.Text(root, "account_number")
                ?? (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0
                    ? SignInProof.Text(results[0], "account_number") : null)
                ?? "verified";
            return SessionIssue.Issued(account, account == "verified" ? "" : account);
        }
    }
}
