using System.Net.Http;
using FluentAssertions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Infrastructure.Crypto;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The key check that runs when someone pastes an API key into a keyed crypto login row.
///
/// <para><b>What makes these tests worth having.</b> Market data at all six venues is public, so a
/// wrong key produces a login that succeeds, charts that fill, and no symptom at all until the first
/// private call. The probe is the only thing standing between a user and that outcome — and a probe
/// that misreads a rejection as a pass is worse than none, because it actively tells them the key is
/// good.</para>
///
/// <para>Every body below is the documented failure shape for that venue. No key is needed to test
/// this: reading a verdict out of a payload is pure, and it is the half that goes wrong.</para>
/// </summary>
public sealed class CryptoAccountProbeTests
{
    private static readonly BrokerCredential Credential = new(
        Key: "key-name", Secret: "c2VjcmV0LXNlY3JldC1zZWNyZXQtc2VjcmV0", Passphrase: "phrase");

    private static readonly DateTimeOffset Now =
        new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    // ── the traps: rejection inside a success status ────────────────────────────────────────────

    [Fact]
    public void Kraken_rejection_arrives_as_http_200_and_is_not_read_as_success()
    {
        // This is the exact shape Kraken returns for a bad key: HTTP 200, errors in the body. Anything
        // that trusts the status line calls this a working key.
        var result = CryptoAccountProbe.Read(
            BrokerKind.Kraken, 200, """{"error":["EAPI:Invalid key"],"result":{}}""");

        result.Ok.Should().BeFalse("Kraken reports a bad key with HTTP 200 and an error array");
        result.Detail.Should().Contain("EAPI:Invalid key");
    }

    [Fact]
    public void Kraken_success_has_an_empty_error_array_rather_than_no_array()
    {
        var result = CryptoAccountProbe.Read(
            BrokerKind.Kraken, 200, """{"error":[],"result":{"ZUSD":"100.0"}}""");

        result.Ok.Should().BeTrue("an empty error array is Kraken's success");
    }

    [Fact]
    public void Okx_rejection_arrives_as_http_200_with_a_non_zero_code()
    {
        var result = CryptoAccountProbe.Read(
            BrokerKind.Okx, 200, """{"code":"50111","msg":"Invalid Sign","data":[]}""");

        result.Ok.Should().BeFalse("OKX reports failure in the body, not the status line");
        result.Detail.Should().Contain("Invalid Sign").And.Contain("50111");
    }

    [Fact]
    public void Okx_success_is_code_zero()
    {
        var result = CryptoAccountProbe.Read(
            BrokerKind.Okx, 200, """{"code":"0","msg":"","data":[{"totalEq":"1000"}]}""");

        result.Ok.Should().BeTrue();
    }

    [Fact]
    public void Okx_code_is_read_whether_it_is_a_string_or_a_number()
    {
        // OKX documents the code as a string. If a response ever carries it unquoted, a parser that
        // only reads strings finds nothing and calls the rejection a pass — so both are read.
        var result = CryptoAccountProbe.Read(
            BrokerKind.Okx, 200, """{"code":50111,"msg":"Invalid Sign"}""");

        result.Ok.Should().BeFalse();
    }

    [Fact]
    public void Deribit_rejection_is_a_json_rpc_error_object()
    {
        var result = CryptoAccountProbe.Read(
            BrokerKind.Deribit, 401,
            """{"error":{"code":13004,"message":"invalid_credentials"},"testnet":false}""");

        result.Ok.Should().BeFalse();
        result.Detail.Should().Contain("invalid_credentials");
    }

    [Fact]
    public void Deribit_success_carries_a_token_and_no_error()
    {
        var result = CryptoAccountProbe.Read(
            BrokerKind.Deribit, 200, """{"result":{"access_token":"abc","expires_in":31536000}}""");

        result.Ok.Should().BeTrue();
    }

    // ── the ordinary shapes ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Binance_repeats_the_venues_own_words_rather_than_the_status_code()
    {
        // "Signature for this request is not valid" tells a user what to fix. "HTTP 401" does not.
        var result = CryptoAccountProbe.Read(
            BrokerKind.Binance, 401,
            """{"code":-1022,"msg":"Signature for this request is not valid."}""");

        result.Ok.Should().BeFalse();
        result.Detail.Should().Contain("Signature for this request is not valid");
    }

    [Fact]
    public void Bybit_message_is_read_from_its_own_field_name()
    {
        var result = CryptoAccountProbe.Read(
            BrokerKind.Bybit, 401, """{"retCode":10003,"retMsg":"API key is invalid."}""");

        result.Ok.Should().BeFalse();
        result.Detail.Should().Contain("API key is invalid");
    }

    [Fact]
    public void An_unparseable_body_still_yields_the_status()
    {
        // A proxy or a maintenance page answers with HTML. That must be a plain failure, not a crash.
        var result = CryptoAccountProbe.Read(BrokerKind.Binance, 502, "<html>Bad Gateway</html>");

        result.Ok.Should().BeFalse();
        result.Detail.Should().Contain("502");
    }

    [Fact]
    public void An_empty_body_with_a_success_status_is_a_pass()
    {
        CryptoAccountProbe.Read(BrokerKind.Binance, 200, string.Empty).Ok.Should().BeTrue();
    }

    // ── the signed requests ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(BrokerKind.Binance)]
    [InlineData(BrokerKind.Bybit)]
    [InlineData(BrokerKind.Okx)]
    [InlineData(BrokerKind.Kraken)]
    [InlineData(BrokerKind.Deribit)]
    public void Every_supported_venue_builds_a_request_carrying_its_key(BrokerKind broker)
    {
        // Coinbase is excluded: its scheme needs a real EC private key to mint a JWT, and it has its
        // own test below. The point here is that no venue silently builds an unauthenticated request.
        using var request = CryptoAccountProbe.Build(broker, Credential, Now);

        var carried = request.RequestUri!.ToString()
            + string.Join(
                ' ', request.Headers.Select(h => $"{h.Key}={string.Join(',', h.Value)}"));

        carried.Should().Contain(Credential.Key, "the venue has to be told which key is signing");
    }

    [Fact]
    public void Binance_signs_exactly_the_query_string_it_sends()
    {
        // The signature covers the query verbatim. If the transmitted query is rebuilt rather than
        // reused, any difference in order or spacing produces a valid-looking signature over text the
        // venue never sees — which is refused as an invalid key.
        using var request = CryptoAccountProbe.Build(BrokerKind.Binance, Credential, Now);

        var query = request.RequestUri!.Query.TrimStart('?');
        var marker = query.IndexOf("&signature=", StringComparison.Ordinal);
        marker.Should().BePositive("the signature is appended after the signed text");

        var signed = query[..marker];
        var sent = query[(marker + "&signature=".Length)..];

        sent.Should().Be(CryptoAuth.BinanceSignature(signed, Credential.Secret));
    }

    [Fact]
    public void Okx_sends_the_passphrase_because_its_keys_carry_one()
    {
        // The passphrase is chosen at key creation and is not the account password. Omitting it fails
        // exactly like a wrong secret, which is why it is asserted rather than assumed.
        using var request = CryptoAccountProbe.Build(BrokerKind.Okx, Credential, Now);

        request.Headers.GetValues("OK-ACCESS-PASSPHRASE").Should().ContainSingle()
            .Which.Should().Be(Credential.Passphrase);
        request.Headers.Contains("OK-ACCESS-SIGN").Should().BeTrue();
    }

    [Fact]
    public async Task Kraken_signs_the_same_nonce_it_posts()
    {
        // The nonce is signed and also sent in the form body. Two different values produce a signature
        // that is internally consistent and still rejected.
        using var request = CryptoAccountProbe.Build(BrokerKind.Kraken, Credential, Now);

        var form = await request.Content!.ReadAsStringAsync(CancellationToken.None);
        var nonce = form["nonce=".Length..];

        request.Headers.GetValues("API-Sign").Should().ContainSingle()
            .Which.Should().Be(
                CryptoAuth.KrakenSignature("/0/private/Balance", nonce, form, Credential.Secret));
    }

    [Fact]
    public void Kraken_probe_posts_rather_than_gets()
    {
        // Kraken's private endpoints are POST-only; a GET is refused in a way that reads like a bad key.
        using var request = CryptoAccountProbe.Build(BrokerKind.Kraken, Credential, Now);

        request.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public void Coinbase_mints_a_bearer_token_bound_to_the_request()
    {
        // A real EC key, generated here, because the JWT cannot be built from a placeholder secret.
        using var key = System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

        var credential = new BrokerCredential(
            Key: "organizations/x/apiKeys/y", Secret: key.ExportECPrivateKeyPem());

        using var request = CryptoAccountProbe.Build(BrokerKind.Coinbase, credential, Now);

        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization.Parameter.Should().NotBeNullOrWhiteSpace()
            .And.Subject.Should().Contain(".", "a JWT is three dot-separated parts");
    }

    // ── the guard rails ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_venue_with_no_keyed_mode_is_reported_rather_than_probed()
    {
        using var http = new HttpClient();

        var result = await CryptoAccountProbe.ProbeAsync(
            http, BrokerKind.Simulated, Credential, Now);

        result.Ok.Should().BeFalse();
        result.Detail.Should().Contain("no keyed mode");
    }

    [Fact]
    public async Task An_empty_credential_is_refused_without_a_network_call()
    {
        // No HttpClient handler is configured, so reaching the network here would throw. It must not.
        using var http = new HttpClient();

        var result = await CryptoAccountProbe.ProbeAsync(
            http, BrokerKind.Binance, BrokerCredential.None, Now);

        result.Ok.Should().BeFalse();
        result.Detail.Should().Contain("No key");
    }

    [Fact]
    public void Support_covers_exactly_the_venues_with_a_keyed_login_row()
    {
        // Six keyed rows exist in the login window. If a seventh is added without a probe, the row
        // accepts a key and checks nothing — this is the test that says so.
        var supported = Enum.GetValues<BrokerKind>()
            .Where(CryptoAccountProbe.Supports)
            .ToArray();

        supported.Should().BeEquivalentTo(new[]
        {
            BrokerKind.Binance, BrokerKind.Coinbase, BrokerKind.Bybit,
            BrokerKind.Kraken, BrokerKind.Okx, BrokerKind.Deribit,
        });
    }
}
