using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Infrastructure;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.ETrade;
using TradingTerminal.Infrastructure.Ig;
using TradingTerminal.Infrastructure.Questrade;
using TradingTerminal.Infrastructure.RobinhoodCrypto;
using TradingTerminal.Infrastructure.Saxo;
using TradingTerminal.Infrastructure.Schwab;
using TradingTerminal.Infrastructure.Tastytrade;
using TradingTerminal.Infrastructure.TradeStation;
using TradingTerminal.Infrastructure.Tradovate;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// What each US and global sign-in sends and how it reads the answer, against a recording handler that
/// answers the way each broker's documentation says it does.
/// </summary>
public sealed class GlobalBrokerSignInTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Records each request (body read, headers kept) and answers from a queue.</summary>
    private sealed class Broker(params (HttpStatusCode Status, string Body, (string, string)[] Headers)[] answers) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode, string, (string, string)[])> _answers = new(answers);

        public List<(HttpMethod Method, string Url, string Body, Dictionary<string, string> Headers)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request.Method, request.RequestUri!.ToString(), body,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(',', h.Value), StringComparer.OrdinalIgnoreCase)));
            var (status, text, headers) = _answers.Count > 0 ? _answers.Dequeue() : (HttpStatusCode.OK, "{}", []);
            var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
            return response;
        }
    }

    private static (HttpStatusCode, string, (string, string)[]) Ok(string body, params (string, string)[] headers) => (HttpStatusCode.OK, body, headers);

    private static async Task<(SessionIssue Issue, Broker Handler)> Run(
        IBrokerSignIn signIn, BrokerCredential app, string proof, params (HttpStatusCode, string, (string, string)[])[] answers)
    {
        var handler = new Broker(answers);
        using var http = new HttpClient(handler);
        var issue = await signIn.SignInAsync(http, app, proof, "https://127.0.0.1", Now, CancellationToken.None);
        return (issue, handler);
    }

    private static Dictionary<string, string> Form(string body) => OAuth1.ReadForm(body);

    [Fact]
    public async Task Schwab_sends_the_decoded_code_under_basic_auth()
    {
        var (issue, broker) = await Run(new SchwabSignIn(), new BrokerCredential("app-key", "app-secret"),
            "https://127.0.0.1/?code=C0.b2F1dGgy%40&session=abc",
            Ok("""{"expires_in":1800,"token_type":"Bearer","scope":"api","refresh_token":"rt","access_token":"at","id_token":"x"}"""));

        var session = KeptSession.Parse(issue.Session)!;
        session.Should().Match<KeptSession>(s => s.AccessToken == "at" && s.RefreshToken == "rt" && s.ExpiresUtc == Now.AddMinutes(30));
        var seen = broker.Seen.Single();
        seen.Url.Should().Be("https://api.schwabapi.com/v1/oauth/token");
        seen.Headers["Authorization"].Should().Be("Basic YXBwLWtleTphcHAtc2VjcmV0", "base64 of app-key:app-secret, as openssl encodes it");
        Form(seen.Body).Should().Contain("code", "C0.b2F1dGgy@").And.Contain("grant_type", "authorization_code").And.Contain("redirect_uri", "https://127.0.0.1");

        new SchwabSignIn().SignInUrl(new BrokerCredential("app key"), "https://127.0.0.1")
            .Should().Be("https://api.schwabapi.com/v1/oauth/authorize?client_id=app%20key&redirect_uri=https%3A%2F%2F127.0.0.1");
    }

    [Fact]
    public async Task Schwab_repeats_the_refusal()
    {
        var (issue, _) = await Run(new SchwabSignIn(), new BrokerCredential("k", "s"), "code=old",
            (HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Bad authorization code"}""", []));

        issue.Ok.Should().BeFalse();
        issue.Detail.Should().Contain("Bad authorization code");
    }

    [Fact]
    public async Task TradeStation_puts_the_client_credentials_in_the_form_and_asks_for_market_data_only()
    {
        var (issue, broker) = await Run(new TradeStationSignIn(), new BrokerCredential("cid", "csecret"), "https://127.0.0.1/?code=xyz&state=daxalgo",
            Ok("""{"access_token":"at","refresh_token":"rt","id_token":"i","token_type":"Bearer","expires_in":1200}"""));

        KeptSession.Parse(issue.Session)!.ExpiresUtc.Should().Be(Now.AddMinutes(20));
        Form(broker.Seen.Single().Body).Should().Contain("client_id", "cid").And.Contain("client_secret", "csecret").And.Contain("code", "xyz");
        broker.Seen.Single().Url.Should().Be("https://signin.tradestation.com/oauth/token");

        new TradeStationSignIn().SignInUrl(new BrokerCredential("cid"), "http://localhost:3000")
            .Should().Contain("audience=https%3A%2F%2Fapi.tradestation.com")
            .And.Contain("scope=openid%20offline_access%20MarketData%20ReadAccount%20Trade");
    }

    [Fact]
    public async Task Tastytrade_exchanges_the_grant_as_json_and_keeps_the_grant_for_renewal()
    {
        var (issue, broker) = await Run(new TastytradeSignIn(), new BrokerCredential("", "client-secret"), " grant-refresh ",
            Ok("""{"access_token":"jwt","token_type":"Bearer","expires_in":900}"""));

        KeptSession.Parse(issue.Session)!.Should().Match<KeptSession>(s => s.AccessToken == "jwt" && s.RefreshToken == "grant-refresh");
        using var sent = JsonDocument.Parse(broker.Seen.Single().Body);
        sent.RootElement.GetProperty("grant_type").GetString().Should().Be("refresh_token");
        sent.RootElement.GetProperty("client_secret").GetString().Should().Be("client-secret");
        sent.RootElement.TryGetProperty("client_id", out _).Should().BeFalse("the client id is optional and was not given");
    }

    [Fact]
    public async Task ETrade_fetches_a_request_token_for_the_page_then_trades_the_code_for_an_access_token()
    {
        var signIn = new ETradeSignIn();
        var handler = new Broker(
            Ok("oauth_token=req%2Btoken&oauth_token_secret=req-secret&oauth_callback_confirmed=true"),
            Ok("oauth_token=acc-token&oauth_token_secret=acc-secret"));
        using var http = new HttpClient(handler);
        var app = new BrokerCredential("ck", "cs");

        var page = await signIn.SignInUrlAsync(http, app, "", Now, CancellationToken.None);
        page.Should().Be("https://us.etrade.com/e/t/etws/authorize?key=ck&token=req%2Btoken");
        handler.Seen[0].Headers["Authorization"].Should().Contain("oauth_callback=\"oob\"");

        var issue = await signIn.SignInAsync(http, app, "AB12C", "", Now, CancellationToken.None);
        KeptSession.Parse(issue.Session)!.Should().Match<KeptSession>(s => s.AccessToken == "acc-token" && s.Secret == "acc-secret");
        handler.Seen[1].Headers["Authorization"].Should().Contain("oauth_verifier=\"AB12C\"").And.Contain("oauth_token=\"req%2Btoken\"");

        (await signIn.SignInAsync(http, app, "AB12C", "", Now, CancellationToken.None)).Ok
            .Should().BeFalse("a request token is spent once");
    }

    [Fact]
    public async Task Tradovate_signs_in_with_the_account_and_key_pair_and_keeps_both_tokens()
    {
        var app = new BrokerCredential("8", "api-sec", "p4ss") { Account = "trader1", Extra = "demo" };
        var (issue, broker) = await Run(new TradovateSignIn(), app, "",
            Ok("""{"accessToken":"at","mdAccessToken":"md","expirationTime":"2026-09-25T13:20:00.000Z","userStatus":"Active","userId":1,"name":"trader1"}"""));

        broker.Seen.Single().Url.Should().Be("https://demo.tradovateapi.com/v1/auth/accesstokenrequest");
        using var sent = JsonDocument.Parse(broker.Seen.Single().Body);
        sent.RootElement.GetProperty("name").GetString().Should().Be("trader1");
        sent.RootElement.GetProperty("password").GetString().Should().Be("p4ss");
        sent.RootElement.GetProperty("cid").GetString().Should().Be("8");
        sent.RootElement.GetProperty("sec").GetString().Should().Be("api-sec");
        sent.RootElement.GetProperty("deviceId").GetString().Should().Be(TradovateSignIn.DeviceId(), "the same device every time");

        issue.Account.Should().Be("trader1");
        KeptSession.Parse(issue.Session)!.Should().Match<KeptSession>(s => s.AccessToken == "at" && s.Extra == "md"
            && s.ExpiresUtc == new DateTimeOffset(2026, 9, 25, 13, 20, 0, TimeSpan.Zero));

        var (refused, _) = await Run(new TradovateSignIn(), app, "", Ok("""{"errorText":"Incorrect username or password. Please try again."}"""));
        refused.Detail.Should().Contain("Incorrect username or password");
    }

    [Fact]
    public async Task Saxo_keeps_the_redirect_for_every_later_refresh()
    {
        var (issue, broker) = await Run(new SaxoSignIn(), new BrokerCredential("appkey", "appsecret"), "https://127.0.0.1/?code=abc&state=daxalgo",
            Ok("""{"access_token":"at","token_type":"Bearer","expires_in":1200,"refresh_token":"rt","refresh_token_expires_in":3600,"base_uri":null}"""));

        KeptSession.Parse(issue.Session)!.Should().Match<KeptSession>(s => s.RefreshToken == "rt" && s.Extra == "https://127.0.0.1");
        Form(broker.Seen.Single().Body).Should().Contain("client_id", "appkey").And.Contain("client_secret", "appsecret").And.Contain("code", "abc");
    }

    [Fact]
    public async Task IG_reads_its_session_from_the_response_headers()
    {
        var app = new BrokerCredential("ig-key", "pw") { Account = "user1" };
        var (issue, broker) = await Run(new IgSignIn(), app, "",
            Ok("""{"accountType":"CFD","currentAccountId":"ABC12","lightstreamerEndpoint":"https://apd.marketdatasystems.com"}""", ("CST", "cst-1"), ("X-SECURITY-TOKEN", "sec-1")));

        KeptSession.Parse(issue.Session)!.Should().Match<KeptSession>(s => s.AccessToken == "cst-1" && s.Secret == "sec-1" && s.Extra == "ABC12");
        var seen = broker.Seen.Single();
        seen.Url.Should().Be("https://api.ig.com/gateway/deal/session");
        seen.Headers["X-IG-API-KEY"].Should().Be("ig-key");
        seen.Headers["Version"].Should().Be("2");
        seen.Body.Should().Contain("\"identifier\":\"user1\"");
    }

    [Fact]
    public async Task Questrade_spends_the_pasted_token_and_keeps_the_new_one_and_the_server()
    {
        var (issue, broker) = await Run(new QuestradeSignIn(), BrokerCredential.None, "pasted-refresh",
            Ok("""{"access_token":"at","token_type":"Bearer","expires_in":1800,"refresh_token":"next-refresh","api_server":"https://api01.iq.questrade.com/"}"""));

        broker.Seen.Single().Method.Should().Be(HttpMethod.Get);
        broker.Seen.Single().Url.Should().Be("https://login.questrade.com/oauth2/token?grant_type=refresh_token&refresh_token=pasted-refresh");
        KeptSession.Parse(issue.Session)!.Should().Match<KeptSession>(s => s.RefreshToken == "next-refresh" && s.Server == "https://api01.iq.questrade.com/");

        var (refused, _) = await Run(new QuestradeSignIn(), BrokerCredential.None, "spent", (HttpStatusCode.BadRequest, "Bad Request", []));
        refused.Detail.Should().Contain("generate a new one");
    }

    [Fact]
    public async Task Robinhood_proves_the_key_pair_with_one_signed_call()
    {
        var app = new BrokerCredential("rh-key", "TM0Imyj/ltqdtsNG7BFOD1uKMZ81q6Yk2oz27U+4pvs=");
        var (issue, broker) = await Run(new RobinhoodCryptoSignIn(), app, "",
            Ok("""{"account_number":"RH123","status":"active","buying_power":"100.00","buying_power_currency":"USD"}"""));

        issue.Should().Be(SessionIssue.Issued("RH123", "RH123"));
        var seen = broker.Seen.Single();
        seen.Url.Should().Be("https://trading.robinhood.com/api/v1/crypto/trading/accounts/");
        var timestamp = long.Parse(seen.Headers["x-timestamp"], System.Globalization.CultureInfo.InvariantCulture);
        timestamp.Should().Be(Now.ToUnixTimeSeconds());
        seen.Headers["x-signature"].Should().Be(RobinhoodSigning.Signature(app.Secret,
            RobinhoodSigning.Message("rh-key", timestamp, "/api/v1/crypto/trading/accounts/", "GET", "")));

        var (bad, none) = await Run(new RobinhoodCryptoSignIn(), new BrokerCredential("k", "not base64!"), "");
        bad.Detail.Should().Contain("not a base64 Ed25519 key");
        none.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task The_issuer_prepares_an_etrade_page_and_says_nothing_when_it_cannot()
    {
        using var http = new HttpClient(new Broker((HttpStatusCode.Unauthorized, "oauth_problem=consumer_key_rejected", [])));
        using var issuer = new BrokerSessionIssuer([new ETradeSignIn(), new SchwabSignIn()], NullLogger<BrokerSessionIssuer>.Instance, http: http);

        (await issuer.SignInUrlAsync(BrokerKind.ETrade, new BrokerCredential("bad", "key"), "")).Should().BeNull();
        (await issuer.SignInUrlAsync(BrokerKind.CharlesSchwab, new BrokerCredential("k"), "https://127.0.0.1")).Should().StartWith("https://api.schwabapi.com/");
        issuer.StyleOf(BrokerKind.Tastytrade).Should().Be(SignInStyle.Token, "a broker the issuer was not given falls back to a pasted token");
    }

    /// <summary>
    /// Every sign-in broker, built the way its registration builds it — so a constructor the container
    /// cannot satisfy fails here rather than when the login window resolves every client at start-up.
    /// </summary>
    [Fact]
    public async Task The_container_builds_every_sign_in_client_and_its_sign_in()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton(IBrokerCredentialSource.None);
        services.AddSingleton(IBrokerSessionStore.None);
        using var provider = services.BuildServiceProvider();

        Type[] clients =
        [
            typeof(RealSchwabClient), typeof(RealTradeStationClient), typeof(RealTastytradeClient), typeof(RealETradeClient),
            typeof(RealTradovateClient), typeof(RealSaxoClient), typeof(RealIgClient), typeof(RealQuestradeClient),
            typeof(RealRobinhoodCryptoClient),
        ];
        foreach (var type in clients)
        {
            var client = (TradingTerminal.Core.MarketData.IBrokerClient)ActivatorUtilities.CreateInstance(provider, type);
            await client.DisposeAsync();
        }

        var full = new ServiceCollection();
        full.AddLogging();
        full.AddOptions();
        full.AddInfrastructureCore();
        full.AddCredentialedBrokers();
        using var composed = full.BuildServiceProvider();
        var issuer = composed.GetRequiredService<IBrokerSessionIssuer>();
        foreach (var kind in new[] { BrokerKind.CharlesSchwab, BrokerKind.TradeStation, BrokerKind.Tastytrade, BrokerKind.ETrade,
                     BrokerKind.Tradovate, BrokerKind.SaxoBank, BrokerKind.IgGroup, BrokerKind.Questrade, BrokerKind.RobinhoodCrypto })
            issuer.Issues(kind).Should().BeTrue($"{kind} has a sign-in registered");
        composed.GetRequiredService<IBrokerSessionStore>().Should().BeSameAs(IBrokerSessionStore.None, "infrastructure alone keeps nothing");
    }
}
