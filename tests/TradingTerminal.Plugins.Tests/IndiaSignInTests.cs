using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Infrastructure.AliceBlue;
using TradingTerminal.Infrastructure.AngelOne;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Dhan;
using TradingTerminal.Infrastructure.FivePaisa;
using TradingTerminal.Infrastructure.Fyers;
using TradingTerminal.Infrastructure.IciciBreeze;
using TradingTerminal.Infrastructure.Zerodha;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// What each Indian sign-in sends, and how it reads the answer — driven against a recording handler that
/// answers the way each broker's documentation says it does.
/// </summary>
public sealed class IndiaSignInTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(59);

    /// <summary>Records each request (with its body read) and answers from a queue.</summary>
    private sealed class Broker(params (HttpStatusCode Status, string Body)[] answers) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode, string)> _answers = new(answers);

        public List<(HttpMethod Method, string Url, string Body, Dictionary<string, string> Headers)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request.Method, request.RequestUri!.ToString(), body,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(',', h.Value), StringComparer.OrdinalIgnoreCase)));
            var (status, text) = _answers.Count > 0 ? _answers.Dequeue() : (HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<(SessionIssue Issue, Broker Handler)> Run(
        IBrokerSignIn signIn, BrokerCredential app, string proof, params (HttpStatusCode, string)[] answers)
    {
        var handler = new Broker(answers);
        using var http = new HttpClient(handler);
        var issue = await signIn.SignInAsync(http, app, proof, "https://127.0.0.1/", Now, CancellationToken.None);
        return (issue, handler);
    }

    [Fact]
    public async Task Zerodha_posts_the_checksum_never_the_secret()
    {
        var (issue, broker) = await Run(new ZerodhaSignIn(), new BrokerCredential("kite-key", "kite-secret"),
            "https://127.0.0.1/?status=success&request_token=req-token",
            (HttpStatusCode.OK, """{"status":"success","data":{"access_token":"at-1","user_id":"AB1234"}}"""));

        issue.Should().Be(SessionIssue.Issued("at-1", "AB1234"));
        var form = broker.Seen.Single();
        form.Url.Should().Be("https://api.kite.trade/session/token");
        form.Body.Should().Contain("request_token=req-token").And.Contain("checksum=" + ZerodhaSignIn.Checksum("kite-key", "req-token", "kite-secret"))
            .And.NotContain("kite-secret");
        form.Headers["X-Kite-Version"].Should().Be("3");
    }

    [Fact]
    public async Task Zerodha_repeats_the_brokers_refusal()
    {
        var (issue, _) = await Run(new ZerodhaSignIn(), new BrokerCredential("k", "s"), "request_token=old",
            (HttpStatusCode.Forbidden, """{"status":"error","message":"Token is invalid or has expired.","error_type":"TokenException"}"""));

        issue.Ok.Should().BeFalse();
        issue.Detail.Should().Contain("Token is invalid or has expired.");
    }

    [Fact]
    public async Task Fyers_sends_the_app_id_hash_and_the_auth_code()
    {
        var (issue, broker) = await Run(new FyersSignIn(), new BrokerCredential("ABCD-100", "fyers-secret"),
            "https://127.0.0.1/?s=ok&code=200&auth_code=code-xyz&state=daxalgo",
            (HttpStatusCode.OK, """{"s":"ok","code":200,"access_token":"fy-token"}"""));

        issue.Session.Should().Be("fy-token");
        using var sent = JsonDocument.Parse(broker.Seen.Single().Body);
        sent.RootElement.GetProperty("appIdHash").GetString().Should().Be(FyersSignIn.AppIdHash("ABCD-100", "fyers-secret"));
        sent.RootElement.GetProperty("code").GetString().Should().Be("code-xyz");
    }

    [Fact]
    public async Task Angel_computes_the_code_from_a_stored_setup_key_and_stores_both_tokens()
    {
        var app = new BrokerCredential("smart-key", "1234", "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ") { Account = "A123" };
        var (issue, broker) = await Run(new AngelOneSignIn(), app, proof: "",
            (HttpStatusCode.OK, """{"status":true,"message":"SUCCESS","data":{"jwtToken":"Bearer eyJjwt","refreshToken":"r","feedToken":"feed-1"}}"""));

        using var sent = JsonDocument.Parse(broker.Seen.Single().Body);
        sent.RootElement.GetProperty("totp").GetString().Should().Be("287082", "RFC 6238 at T=59");
        sent.RootElement.GetProperty("clientcode").GetString().Should().Be("A123");
        broker.Seen.Single().Headers["X-PrivateKey"].Should().Be("smart-key");

        AngelSession.Read(issue.Session).Should().Be(new AngelSession("eyJjwt", "feed-1"), "the Bearer prefix is stored bare");
    }

    [Fact]
    public async Task Angel_without_a_code_or_a_setup_key_asks_for_the_code()
    {
        var (issue, broker) = await Run(new AngelOneSignIn(), new BrokerCredential("k", "1234") { Account = "A1" }, proof: "");

        issue.Ok.Should().BeFalse();
        broker.Seen.Should().BeEmpty("nothing is sent without a code");
    }

    [Fact]
    public async Task FivePaisa_exchanges_the_request_token_with_the_encryption_key_and_user_id()
    {
        var app = new BrokerCredential("user-key", "encry-key", "5678") { Account = "CLIENT1", Extra = "APPUSER" };
        var (issue, broker) = await Run(new FivePaisaSignIn(), app, "123456",
            (HttpStatusCode.OK, """{"head":{"status":"0"},"body":{"RequestToken":"rt-1","Status":0}}"""),
            (HttpStatusCode.OK, """{"head":{"status":"0"},"body":{"AccessToken":"access-1","ClientCode":"CLIENT1"}}"""));

        issue.Should().Be(SessionIssue.Issued("access-1", "CLIENT1"));
        broker.Seen[0].Url.Should().EndWith("/TOTPLogin");
        broker.Seen[0].Body.Should().Contain("\"TOTP\":\"123456\"").And.Contain("\"PIN\":\"5678\"").And.Contain("\"Key\":\"user-key\"");
        broker.Seen[1].Url.Should().EndWith("/GetAccessToken");
        broker.Seen[1].Body.Should().Contain("\"RequestToken\":\"rt-1\"").And.Contain("\"EncryKey\":\"encry-key\"").And.Contain("\"UserId\":\"APPUSER\"");
    }

    [Fact]
    public async Task AliceBlue_hashes_user_key_and_encryption_key_into_user_data()
    {
        var (issue, broker) = await Run(new AliceBlueSignIn(), new BrokerCredential("", "alice-key") { Account = "ab123" }, "",
            (HttpStatusCode.OK, """{"encKey":"enc-key","stat":"Ok"}"""),
            (HttpStatusCode.OK, """{"sessionID":"sid-1","stat":"Ok"}"""));

        issue.Should().Be(SessionIssue.Issued("sid-1", "AB123"));
        using var second = JsonDocument.Parse(broker.Seen[1].Body);
        second.RootElement.GetProperty("userData").GetString().Should().Be(AliceBlueSignIn.UserData("AB123", "alice-key", "enc-key"));
    }

    [Fact]
    public async Task Breeze_exchanges_the_api_session_in_a_get_with_a_body()
    {
        var (issue, broker) = await Run(new IciciBreezeSignIn(), new BrokerCredential("app-key", "secret"),
            "https://127.0.0.1/?apisession=48512345",
            (HttpStatusCode.OK, """{"Success":{"session_token":"QUIxMjM6NDg1MTIzNDU=","idirect_userid":"AB123"},"Status":200,"Error":null}"""));

        issue.Should().Be(SessionIssue.Issued("QUIxMjM6NDg1MTIzNDU=", "AB123"));
        broker.Seen.Single().Method.Should().Be(HttpMethod.Get);
        broker.Seen.Single().Body.Should().Contain("\"SessionToken\":\"48512345\"").And.Contain("\"AppKey\":\"app-key\"");
    }

    [Fact]
    public async Task Dhan_checks_a_pasted_token_before_keeping_it()
    {
        var (good, broker) = await Run(new DhanSignIn(), new BrokerCredential { Account = "1000123" }, "tok-1", (HttpStatusCode.OK, "{}"));
        good.Should().Be(SessionIssue.Issued("tok-1", "1000123"));
        broker.Seen.Single().Headers["access-token"].Should().Be("tok-1");

        var (bad, _) = await Run(new DhanSignIn(), new BrokerCredential { Account = "1000123" }, "stale",
            (HttpStatusCode.Unauthorized, """{"errorType":"Invalid_Authentication","errorMessage":"Client ID or user generated access token is invalid or expired."}"""));
        bad.Ok.Should().BeFalse();
        bad.Detail.Should().Contain("expired");
    }

    [Fact]
    public async Task The_issuer_turns_a_failure_into_a_refusal_rather_than_an_exception()
    {
        using var http = new HttpClient(new ThrowingHandler());
        using var issuer = new BrokerSessionIssuer([new ZerodhaSignIn()], NullLogger<BrokerSessionIssuer>.Instance, http: http);

        var issue = await issuer.SignInAsync(BrokerKind.Zerodha, new BrokerCredential("k", "s"), "request_token=x", "https://127.0.0.1/");

        issue.Ok.Should().BeFalse();
        issue.Detail.Should().Contain("could not be established");
        issuer.StyleOf(BrokerKind.Zerodha).Should().Be(SignInStyle.Browser);
        (await issuer.SignInUrlAsync(BrokerKind.Zerodha, new BrokerCredential("kite key"), ""))
            .Should().Be("https://kite.zerodha.com/connect/login?v=3&api_key=kite%20key");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("The SSL connection could not be established.");
    }
}
