using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;
using TradingTerminal.Infrastructure.RobinhoodCrypto;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The pieces the US and global brokers share: Ed25519 (Robinhood), OAuth 1.0a (E*TRADE), the streamed-JSON
/// splitter (TradeStation) and the session keeper that renews short-lived tokens and writes rotated ones
/// back. Expected values are published test vectors or were computed with OpenSSL 3.5, never by the code
/// under test.
/// </summary>
public sealed class GlobalBrokerSigningTests
{
    // ── Ed25519 (RFC 8032 §7.1) ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData( // TEST 1 — the empty message
        "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
        "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
        "",
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
    [InlineData( // TEST 2 — also reproduced with OpenSSL
        "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
        "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
        "72",
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
    [InlineData( // TEST 3 — also reproduced with OpenSSL
        "c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7",
        "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025",
        "af82",
        "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a")]
    public void Ed25519_matches_the_rfc_8032_vectors(string seed, string publicKey, string message, string signature)
    {
        var secret = Convert.FromHexString(seed);
        Convert.ToHexStringLower(Ed25519.PublicKey(secret)).Should().Be(publicKey);
        Convert.ToHexStringLower(Ed25519.Sign(secret, Convert.FromHexString(message))).Should().Be(signature);
    }

    [Fact]
    public void Ed25519_accepts_a_64_byte_libsodium_key_and_refuses_anything_else()
    {
        var seed = Convert.FromHexString("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb");
        Ed25519.SeedFrom([.. seed, .. Ed25519.PublicKey(seed)]).Should().Equal(seed);
        FluentActions.Invoking(() => Ed25519.SeedFrom(new byte[31])).Should().Throw<FormatException>();
    }

    [Fact]
    public void Robinhood_signs_key_timestamp_path_method_body_as_openssl_does()
    {
        var message = RobinhoodSigning.Message("rh-key", 1_790_000_000, "/api/v1/crypto/trading/accounts/", "GET", "");
        message.Should().Be("rh-key1790000000/api/v1/crypto/trading/accounts/GET");

        // openssl pkeyutl -sign -rawin with the RFC 8032 TEST 2 key.
        RobinhoodSigning.Signature("TM0Imyj/ltqdtsNG7BFOD1uKMZ81q6Yk2oz27U+4pvs=", message)
            .Should().Be("h4wtFldVv1rkdVs/ye/XbXNerGiGjUAUOlmadUF5CiqKLWLpVsSOADXkX/Z0Qms3ZZ+jQwephaaXDmjoSV9AAQ==");
    }

    // ── OAuth 1.0a (RFC 5849) ────────────────────────────────────────────────────────────────────

    private static readonly (string, string)[] RfcParameters =
    [
        ("oauth_consumer_key", "9djdj82h48djs9d2"), ("oauth_token", "kkk9d7dh3k39sjv7"),
        ("oauth_signature_method", "HMAC-SHA1"), ("oauth_timestamp", "137131201"), ("oauth_nonce", "7d8f3e4a"),
        // The RFC's example also signs two form-body parameters.
        ("c2", ""), ("a3", "2 q"),
    ];

    [Fact]
    public void OAuth1_builds_the_rfc_5849_signature_base_string()
    {
        OAuth1.BaseString("POST", "http://example.com/request?b5=%3D%253D&a3=a&c%40=&a2=r%20b", RfcParameters)
            .Should().Be("POST&http%3A%2F%2Fexample.com%2Frequest&a2%3Dr%2520b%26a3%3D2%2520q%26a3%3Da%26b5%3D%253D%25253D"
                + "%26c%2540%3D%26c2%3D%26oauth_consumer_key%3D9djdj82h48djs9d2%26oauth_nonce%3D7d8f3e4a"
                + "%26oauth_signature_method%3DHMAC-SHA1%26oauth_timestamp%3D137131201%26oauth_token%3Dkkk9d7dh3k39sjv7");
    }

    [Fact]
    public void OAuth1_signs_with_both_secrets_as_openssl_does()
    {
        // printf '%s' "$BASE" | openssl dgst -sha1 -hmac 'j49sk3j29djd&dh893hdasih9' -binary | base64
        OAuth1.Signature("POST", "http://example.com/request?b5=%3D%253D&a3=a&c%40=&a2=r%20b", RfcParameters, "j49sk3j29djd", "dh893hdasih9")
            .Should().Be("r6/TJjbCOr97/+UU0NsvSne7s5g=");
    }

    [Fact]
    public void OAuth1_header_carries_every_oauth_parameter_and_the_signature()
    {
        var header = OAuth1.Authorization("GET", "https://api.etrade.com/oauth/request_token", "ck", "cs", "", "", 1_700_000_000, "n0nce",
            ("oauth_callback", "oob"));

        header.Should().StartWith("OAuth realm=\"\",")
            .And.Contain("oauth_consumer_key=\"ck\"").And.Contain("oauth_callback=\"oob\"").And.Contain("oauth_nonce=\"n0nce\"")
            .And.Contain("oauth_signature=\"").And.NotContain("oauth_token=", "there is no token before the request token");
        OAuth1.ReadForm("oauth_token=a%2Bb&oauth_token_secret=s").Should().Contain("oauth_token", "a+b");
    }

    // ── Streamed JSON ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_splitter_finds_whole_objects_across_any_chunk_boundary()
    {
        const string stream = "{\"Symbol\":\"A}\\\"{\",\"Bids\":[{\"P\":\"1\"}]}\r\n{\"Heartbeat\":1}{\"x\":{\"y\":[1,{\"z\":\"]\"}]}}";
        var bytes = Encoding.UTF8.GetBytes(stream);

        for (var cut = 1; cut < bytes.Length; cut++)
        {
            var splitter = new JsonObjectSplitter();
            var objects = splitter.Push(bytes.AsSpan(0, cut)).Concat(splitter.Push(bytes.AsSpan(cut))).ToList();
            objects.Should().Equal(
                ["{\"Symbol\":\"A}\\\"{\",\"Bids\":[{\"P\":\"1\"}]}", "{\"Heartbeat\":1}", "{\"x\":{\"y\":[1,{\"z\":\"]\"}]}}"],
                $"cut at byte {cut}");
        }
    }

    // ── Session keeper ───────────────────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A credential store the test can rewrite, and which can refuse writes.</summary>
    private sealed class Store(string session) : IBrokerCredentialSource, IBrokerSessionStore
    {
        public string Session { get; set; } = session;

        public bool RefuseWrites { get; set; }

        public List<string> Written { get; } = [];

        public BrokerCredential For(BrokerKind broker) => new("key", "secret") { Session = Session };

        public void Renew(BrokerKind broker, string session)
        {
            Written.Add(session);
            if (!RefuseWrites) Session = session;
        }
    }

    private static string Session(string access, string refresh, DateTimeOffset expires) =>
        new KeptSession { AccessToken = access, RefreshToken = refresh, ExpiresUtc = expires }.ToJson();

    private static (SessionKeeper Keeper, List<string> Renewals) Keeper(Store store)
    {
        var renewals = new List<string>();
        var keeper = new SessionKeeper(BrokerKind.Questrade, store, store, async (app, s, ct) =>
        {
            await Task.Delay(20, ct);
            lock (renewals) renewals.Add(s.RefreshToken);
            return new KeptSession { AccessToken = "access-" + (renewals.Count + 1), RefreshToken = "refresh-" + (renewals.Count + 1), ExpiresUtc = Now.AddMinutes(30) };
        }, NullLogger.Instance, new Clock(Now));
        return (keeper, renewals);
    }

    [Fact]
    public async Task A_fresh_session_is_used_as_it_is()
    {
        var store = new Store(Session("a", "r", Now.AddMinutes(10)));
        var (keeper, renewals) = Keeper(store);

        (await keeper.CurrentAsync(default)).AccessToken.Should().Be("a");
        renewals.Should().BeEmpty();
    }

    [Fact]
    public async Task An_expiring_session_is_renewed_once_however_many_ask_and_written_back()
    {
        var store = new Store(Session("a", "r1", Now.AddSeconds(30)));
        var (keeper, renewals) = Keeper(store);

        var sessions = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => keeper.CurrentAsync(default)));

        renewals.Should().Equal(["r1"], "one renewal, spending the stored refresh token");
        sessions.Select(s => s.AccessToken).Should().AllBe("access-2");
        KeptSession.Parse(store.Session)!.RefreshToken.Should().Be("refresh-2", "the rotated refresh token is stored");
    }

    [Fact]
    public async Task A_failed_write_back_does_not_send_the_keeper_back_to_the_dead_token()
    {
        var store = new Store(Session("a", "r1", Now.AddSeconds(30))) { RefuseWrites = true };
        var (keeper, renewals) = Keeper(store);

        (await keeper.CurrentAsync(default)).AccessToken.Should().Be("access-2");
        (await keeper.CurrentAsync(default)).AccessToken.Should().Be("access-2", "the renewed session is kept in memory");
        renewals.Should().HaveCount(1, "the stale stored token is not spent again");
    }

    [Fact]
    public async Task A_new_sign_in_is_picked_up_while_running()
    {
        var store = new Store(Session("a", "r", Now.AddMinutes(10)));
        var (keeper, _) = Keeper(store);
        await keeper.CurrentAsync(default);

        store.Session = Session("b", "r-new", Now.AddMinutes(10));
        (await keeper.CurrentAsync(default)).AccessToken.Should().Be("b");
    }

    [Fact]
    public async Task A_refused_token_is_renewed_on_the_next_call()
    {
        var store = new Store(Session("a", "r", Now.AddMinutes(10)));
        var (keeper, renewals) = Keeper(store);

        var first = await keeper.CurrentAsync(default);
        keeper.Invalidate(first);
        (await keeper.CurrentAsync(default)).AccessToken.Should().Be("access-2");
        renewals.Should().Equal(["r"]);
    }

    [Fact]
    public async Task No_stored_session_says_sign_in_again()
    {
        var (keeper, _) = Keeper(new Store(""));
        await FluentActions.Awaiting(() => keeper.CurrentAsync(default))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*sign in again*");
    }

    [Fact]
    public void An_oauth_answer_without_a_new_refresh_token_keeps_the_old_one()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"access_token":"x","expires_in":1200}""");
        var session = OAuthTokens.Read(doc.RootElement, Now, new KeptSession { RefreshToken = "keep", Extra = "https://127.0.0.1/" });

        session.Should().Be(new KeptSession { AccessToken = "x", RefreshToken = "keep", ExpiresUtc = Now.AddSeconds(1200), Extra = "https://127.0.0.1/" });

        using var refusal = System.Text.Json.JsonDocument.Parse("""{"error":"invalid_grant","error_description":"Refresh token expired"}""");
        FluentActions.Invoking(() => OAuthTokens.Read(refusal.RootElement, Now, broker: "Saxo"))
            .Should().Throw<InvalidOperationException>().WithMessage("Saxo issued no access token: Refresh token expired");
    }

    [Fact]
    public void Quote_bars_build_ohlc_and_volume_from_polled_quotes()
    {
        var bars = new QuoteBars(TimeSpan.FromMinutes(1));
        var t = new DateTime(2026, 9, 25, 14, 30, 5, DateTimeKind.Utc);

        bars.Push(new PolledQuote(t, 99, 101, 0, 0, Last: 100, DayVolume: 1_000));
        bars.Push(new PolledQuote(t.AddSeconds(10), 101, 103, 0, 0, Last: 102, DayVolume: 1_250));
        var bar = bars.Push(new PolledQuote(t.AddSeconds(20), 97, 99, 0, 0, DayVolume: 1_300))!;

        bar.Should().Be(new Bar(new DateTime(2026, 9, 25, 14, 30, 0, DateTimeKind.Utc), 100, 102, 98, 98, 300),
            "the mid stands in for a missing last, and volume is the rise in the day's total after the first sample");
        bars.Push(new PolledQuote(t.AddMinutes(1), 1, 3, 0, 0))!.Open.Should().Be(2, "a new minute opens a new bar");
    }
}
