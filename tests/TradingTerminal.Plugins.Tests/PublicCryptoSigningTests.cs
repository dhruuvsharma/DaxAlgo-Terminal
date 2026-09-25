using FluentAssertions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Infrastructure.Crypto;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Signing and key checking for the twelve venues added on 2026-09-25.
///
/// <para><b>What the expected signatures are.</b> Each was computed with <c>openssl dgst -hmac</c> over the
/// exact string the venue's documentation says to sign — an independent implementation, so a slip in how
/// this code concatenates, encodes or hashes shows up here. They are not the venues' own worked examples:
/// two remembered from documentation did not reproduce under openssl, so neither is trusted, and this says
/// so rather than presenting a guess as a published vector. Whether each documented scheme is what the
/// venue actually checks is what a real key settles, which is why these venues are Unverified.</para>
///
/// <para>The refusal bodies are the ones each venue sent to a made-up key on 2026-09-25 — see
/// <c>PublicCryptoVenuesLiveRun</c>.</para>
/// </summary>
public sealed class PublicCryptoSigningTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1790274000000);

    private static readonly BrokerCredential Credential = new("probe-key", "probe-secret", "probe-pass");

    // ── signatures ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bitget_signs_timestamp_method_and_path_as_base64() =>
        CryptoAuth.BitgetSignature("1790274000000", "get", "/api/v2/spot/account/assets", "", "bitget-secret")
            .Should().Be("brEzc+ysgkDwUJnCT9u3pA52oO8bbhInt6826XmfJGY=");

    [Fact]
    public void KuCoin_signs_the_request_and_signs_the_passphrase_too()
    {
        CryptoAuth.KuCoinSignature("1790274000000", "GET", "/api/v1/accounts", "", "kucoin-secret")
            .Should().Be("1IQclfd7ZIzIsIT5X47cdnJKnoEiZ5mElgGxVJwDuv8=");
        CryptoAuth.KuCoinPassphrase("kucoin-pass", "kucoin-secret")
            .Should().Be("OplStk3M/IYYslJlMpTMb7auKQZQhD1g+JEQkER3KkA=");
    }

    [Fact]
    public void GateIo_signs_the_hash_of_an_empty_body_not_nothing() =>
        CryptoAuth.GateIoSignature("GET", "/api/v4/spot/accounts", "", "", "1790274000", "gate-secret")
            .Should().Be("a1ac53ee45ebdeeade3145cd36438e87ebb44e02402ac11e18f385070464f69c4f1e22e39ca29312a28eae25cae9bb23df5611b420e82ee71b629788005481e8");

    [Fact]
    public void Gemini_signs_the_base64_payload_not_the_json()
    {
        var (payload, signature) = CryptoAuth.GeminiSignature("""{"request":"/v1/balances","nonce":1790274000000}""", "gemini-secret");

        payload.Should().Be("eyJyZXF1ZXN0IjoiL3YxL2JhbGFuY2VzIiwibm9uY2UiOjE3OTAyNzQwMDAwMDB9");
        signature.Should().Be("46dbce9d733118655749d03b6c85322fe7a61d6fee82347836d104988175ab2999fbea36a5de22d3465a000fd177cd5b");
    }

    [Fact]
    public void CryptoCom_signs_method_id_key_params_nonce() =>
        CryptoAuth.CryptoComSignature("private/user-balance", "1", "cdc-key", "", "1790274000000", "cdc-secret")
            .Should().Be("d672598d0ba69f21c364ebf71f0066903f8970e5523d7cc077df520808c93a29");

    [Fact]
    public void Bitfinex_signs_the_api_prefix_the_path_does_not_show() =>
        CryptoAuth.BitfinexSignature("v2/auth/r/wallets", "1790274000000000", "{}", "bfx-secret")
            .Should().Be("ab0a3830cb36bd27f4a94d072fad7312a3d1fb35cc41a10472463bdaea4f633f7b1b4b092609b13abcf168483e715569");

    [Fact]
    public void Bitstamp_leaves_the_content_type_out_when_there_is_no_body() =>
        CryptoAuth.BitstampSignature("bs-key", "POST", "www.bitstamp.net", "/api/v2/account_balances/", "",
                "", "7c1a3f2e-1111-4222-8333-944455556666", "1790274000000", "", "bs-secret")
            .Should().Be("a0b142f3b0f43b4c9d4a02b7e27bd817158a5c892526b40ad57262d87e6017b8");

    [Fact]
    public void Bitvavo_signs_timestamp_method_and_the_v2_path() =>
        CryptoAuth.BitvavoSignature("1790274000000", "GET", "/v2/balance", "", "bv-secret")
            .Should().Be("1012bb883bbf27889cc4790a658ef6d895e9dce1835b26476962cf73885afa36");

    [Fact]
    public void Htx_signs_a_sorted_query_with_upper_case_escapes()
    {
        var query = CryptoAuth.HtxQuery(
        [
            new("Timestamp", CryptoAuth.HtxTimestamp(new DateTimeOffset(2026, 9, 25, 1, 0, 0, TimeSpan.Zero))),
            new("SignatureVersion", "2"),
            new("AccessKeyId", "htx-key"),
            new("SignatureMethod", "HmacSHA256"),
        ]);

        query.Should().Be("AccessKeyId=htx-key&SignatureMethod=HmacSHA256&SignatureVersion=2&Timestamp=2026-09-25T01%3A00%3A00");
        CryptoAuth.HtxSignature("GET", "API.HUOBI.PRO", "/v1/account/accounts", query, "htx-secret")
            .Should().Be("rZBPMyNs/nkeY0PA3wJqP4p0vQmAEeffrjGYG5IcvAw=", "the host is signed lower-case");
    }

    [Fact]
    public void Mexc_signs_the_query_as_binance_does() =>
        CryptoAuth.MexcSignature("timestamp=1790274000000&recvWindow=5000", "mexc-secret")
            .Should().Be("454aeab6cc9a170f62940827104b8e6ed65e14310062e1b31ef781d2ee456cea");

    [Fact]
    public void Upbit_style_jwt_is_hs256_over_the_claims() =>
        CryptoAuth.JwtHs256([new("access_key", "up-key"), new("nonce", "n-1")], "up-secret")
            .Should().Be("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJhY2Nlc3Nfa2V5IjoidXAta2V5Iiwibm9uY2UiOiJuLTEifQ.MJSOqMG2_mrDk3Xv_RViqLKm84Q-Ik4Wl4QpgiMJ1Mg");

    // ── requests ────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<BrokerKind> NewVenues() =>
    [
        BrokerKind.Bitget, BrokerKind.KuCoin, BrokerKind.GateIo, BrokerKind.Gemini, BrokerKind.CryptoCom,
        BrokerKind.Upbit, BrokerKind.Bithumb, BrokerKind.Bitfinex, BrokerKind.Bitstamp, BrokerKind.Bitvavo,
        BrokerKind.Htx, BrokerKind.Mexc,
    ];

    [Theory]
    [MemberData(nameof(NewVenues))]
    public async Task Every_new_venue_is_supported_and_builds_a_request_that_names_its_key(BrokerKind venue)
    {
        CryptoAccountProbe.Supports(venue).Should().BeTrue();

        using var request = CryptoAccountProbe.Build(venue, Credential, Now);
        var carried = request.RequestUri + " "
            + string.Join(' ', request.Headers.Select(h => $"{h.Key}={string.Join(',', h.Value)}"))
            + (request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync());

        // The JWT venues (Upbit, Bithumb) carry the key inside the token's claims.
        var claims = request.Headers.Authorization?.Parameter?.Split('.') is [_, var payload, _]
            ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Pad(payload)))
            : string.Empty;
        var named = carried.Contains(Credential.Key, StringComparison.Ordinal)
            || claims.Contains(Credential.Key, StringComparison.Ordinal);

        named.Should().BeTrue($"{venue} has to be told which key is signing: {carried}");
    }

    private static string Pad(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        return s + new string('=', (4 - s.Length % 4) % 4);
    }

    [Fact]
    public void KuCoin_sends_the_passphrase_signed_for_a_version_two_key()
    {
        using var request = CryptoAccountProbe.Build(BrokerKind.KuCoin, Credential, Now);

        request.Headers.GetValues("KC-API-KEY-VERSION").Single().Should().Be("2");
        request.Headers.GetValues("KC-API-PASSPHRASE").Single()
            .Should().Be(CryptoAuth.KuCoinPassphrase(Credential.Passphrase, Credential.Secret)).And.NotBe(Credential.Passphrase);
    }

    [Fact]
    public void Gemini_carries_the_whole_request_in_a_header()
    {
        using var request = CryptoAccountProbe.Build(BrokerKind.Gemini, Credential, Now);

        var payload = request.Headers.GetValues("X-GEMINI-PAYLOAD").Single();
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload))
            .Should().Be("""{"request":"/v1/balances","nonce":1790274000000}""");
        request.Headers.GetValues("X-GEMINI-SIGNATURE").Single()
            .Should().Be(CryptoAuth.GeminiSignature("""{"request":"/v1/balances","nonce":1790274000000}""", Credential.Secret).Signature);
    }

    [Fact]
    public void Bitstamp_sends_no_content_type_for_its_empty_body()
    {
        using var request = CryptoAccountProbe.Build(BrokerKind.Bitstamp, Credential, Now);

        request.Content.Should().BeNull("a content type sent with no body is itself refused");
        request.Headers.GetValues("X-Auth-Version").Single().Should().Be("v2");
        request.Headers.GetValues("X-Auth-Nonce").Single().Should().HaveLength(36);
    }

    [Fact]
    public void Htx_appends_the_signature_url_encoded_after_the_signed_query()
    {
        using var request = CryptoAccountProbe.Build(BrokerKind.Htx, Credential, Now);

        var query = request.RequestUri!.Query.TrimStart('?');
        var at = query.IndexOf("&Signature=", StringComparison.Ordinal);
        var signed = query[..at];
        var sent = Uri.UnescapeDataString(query[(at + "&Signature=".Length)..]);

        sent.Should().Be(CryptoAuth.HtxSignature("GET", "api.huobi.pro", "/v1/account/accounts", signed, Credential.Secret));
    }

    // ── refusals, as each venue sent them to a made-up key ──────────────────────────────────────

    [Theory]
    [InlineData(BrokerKind.Bitget, 400, """{"code":"40037","msg":"Apikey does not exist","requestTime":1790277000000,"data":null}""", "Apikey does not exist")]
    [InlineData(BrokerKind.KuCoin, 401, """{"code":"400003","msg":"The API key does not exist or site mismatch."}""", "400003")]
    [InlineData(BrokerKind.GateIo, 401, """{"label":"INVALID_KEY","message":"Invalid key provided"}""", "INVALID_KEY")]
    [InlineData(BrokerKind.Gemini, 400, """{"result":"error","reason":"InvalidApiKey","message":"Invalid API key"}""", "InvalidApiKey")]
    [InlineData(BrokerKind.CryptoCom, 401, """{"id":1,"method":"private/user-balance","code":40101,"message":"Authentication failure"}""", "40101")]
    [InlineData(BrokerKind.Upbit, 401, """{"error":{"name":"invalid_jwt"}}""", "invalid_jwt")]
    [InlineData(BrokerKind.Bithumb, 401, """{"error":{"name":"invalid_access_key"}}""", "invalid_access_key")]
    [InlineData(BrokerKind.Bitfinex, 500, """["error",10100,"apikey: digest invalid"]""", "digest invalid")]
    [InlineData(BrokerKind.Bitstamp, 403, """{"status": "error", "reason": "Invalid signature", "code": "API0005"}""", "API0005")]
    [InlineData(BrokerKind.Bitvavo, 403, """{"errorCode":301,"error":"API Key must be of length 64."}""", "301")]
    [InlineData(BrokerKind.Htx, 200, """{"status":"error","err-code":"api-signature-not-valid","err-msg":"Signature not valid: Incorrect Access key","data":null}""", "api-signature-not-valid")]
    [InlineData(BrokerKind.Mexc, 400, """{"code":10072,"msg":"Api key info invalid"}""", "Api key info invalid")]
    public void A_refusal_is_read_as_a_refusal_in_the_venues_words(BrokerKind venue, int status, string body, string expected)
    {
        var result = CryptoAccountProbe.Read(venue, status, body);

        result.Ok.Should().BeFalse();
        result.Reached.Should().BeTrue("the venue answered — that is a verdict about the key");
        result.Detail.Should().Contain(expected);
    }

    [Theory]
    [InlineData(BrokerKind.Bitget, """{"code":"00000","msg":"success","data":[]}""")]
    [InlineData(BrokerKind.KuCoin, """{"code":"200000","data":[]}""")]
    [InlineData(BrokerKind.Htx, """{"status":"ok","data":[]}""")]
    [InlineData(BrokerKind.CryptoCom, """{"id":1,"method":"private/user-balance","code":0,"result":{"data":[]}}""")]
    [InlineData(BrokerKind.Upbit, "[]")]
    [InlineData(BrokerKind.Bitfinex, "[]")]
    public void A_venues_success_shape_is_a_pass(BrokerKind venue, string body) =>
        CryptoAccountProbe.Read(venue, 200, body).Ok.Should().BeTrue();

    [Fact]
    public void Bybit_explains_itself_in_the_status_line_with_an_empty_body()
    {
        var result = CryptoAccountProbe.Read(BrokerKind.Bybit, 401, string.Empty, "API key is invalid.");

        result.Reached.Should().BeTrue();
        result.Detail.Should().Contain("API key is invalid.");
    }

    // ── no verdict is not a refusal ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(503, "<html>Service Unavailable</html>")]
    [InlineData(502, "")]
    [InlineData(429, """{"msg":"Too many requests"}""")]
    [InlineData(403, "<html><title>Just a moment...</title></html>")]
    public void A_fault_a_throttle_or_an_edge_page_is_not_the_venue_saying_no(int status, string body)
    {
        var result = CryptoAccountProbe.Read(BrokerKind.Mexc, status, body);

        result.Ok.Should().BeFalse();
        result.Reached.Should().BeFalse("telling someone with a good key to regenerate it is worse than not checking");
    }

    [Fact]
    public void A_bare_401_is_still_a_refusal()
    {
        // "Unauthorised" is an answer about the key whatever the body says.
        CryptoAccountProbe.Read(BrokerKind.Mexc, 401, string.Empty).Reached.Should().BeTrue();
    }

    [Fact]
    public async Task An_unreachable_host_is_reported_as_unreached_and_the_verifier_does_not_refuse()
    {
        using var http = new HttpClient(new FailingHandler());
        var result = await CryptoAccountProbe.ProbeAsync(http, BrokerKind.Bitget, Credential, Now);

        result.Reached.Should().BeFalse();

        using var verifier = new CryptoCredentialVerifier(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CryptoCredentialVerifier>.Instance, http: new HttpClient(new FailingHandler()));
        var verification = await verifier.VerifyAsync(BrokerKind.Bitget, Credential);

        verification.IsRefusal.Should().BeFalse("a connection that never completed is not the venue refusing the key");
        verification.Checked.Should().BeFalse();
    }

    [Fact]
    public async Task A_dropped_handshake_is_retried_once_with_a_fresh_request()
    {
        var handler = new FailingHandler(failures: 1);
        using var http = new HttpClient(handler);

        var result = await CryptoAccountProbe.ProbeAsync(http, BrokerKind.Bitget, Credential, Now);

        handler.Calls.Should().Be(2);
        result.Ok.Should().BeTrue();
    }

    private sealed class FailingHandler(int failures = int.MaxValue) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (Calls <= failures) throw new HttpRequestException("The SSL connection could not be established.");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":"00000","data":[]}"""),
            });
        }
    }
}
