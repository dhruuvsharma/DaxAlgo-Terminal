using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Infrastructure.Crypto;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Request signing for the keyed crypto venues, checked against the worked examples the venues publish.
///
/// <para>This is the rare part of a broker integration that can be proved without an account. A
/// signature is a pure function of key, time and request, and three of these five publish a vector with
/// a known secret and an expected output — so the code is either right or the test fails, today, rather
/// than being discovered wrong later against somebody's real key.</para>
///
/// <para>It matters more than usual because the failure mode carries no information: a signature wrong
/// in any detail is rejected exactly like a stolen one. There is nothing to debug against in the field.</para>
/// </summary>
public sealed class CryptoAuthTests
{
    // ── Binance ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BinanceMatchesThePublishedVector()
    {
        // Straight from the endpoint-security documentation.
        const string secret = "NhqPtmdSJYdKjVHjA7PZj4Mge3R5YNiP1e3UZjInClVN65XAbvqqM6A7H5fATj0j";
        const string query =
            "symbol=LTCBTC&side=BUY&type=LIMIT&timeInForce=GTC&quantity=1&price=0.1"
            + "&recvWindow=5000&timestamp=1499827319559";

        CryptoAuth.BinanceSignature(query, secret)
            .Should().Be("c8db56825ae71d6d79447849e617115f4a920fa2acdcab2b053c4b2838bd6b71");
    }

    [Fact]
    public void BinanceSignsExactlyWhatWillBeSent()
    {
        // Re-ordering after signing is how you produce a valid signature for a different request.
        const string secret = "secret";

        CryptoAuth.BinanceSignature("a=1&b=2", secret)
            .Should().NotBe(CryptoAuth.BinanceSignature("b=2&a=1", secret));
    }

    // ── Bybit ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BybitConcatenatesTheFourPartsInTheDocumentedOrder()
    {
        // The parts are unlabelled in the signed string, so a swapped key and timestamp still produce a
        // well-formed signature — one that is simply wrong, with nothing to say why.
        const string secret = "abcdefghijklmnopqrstuvwxyz1234567890";
        const string timestamp = "1658384314791";
        const string apiKey = "XXXXXXXXXX";
        const string recvWindow = "5000";
        const string query = "category=option&symbol=BTC-29JUL22-25000-C";

        var expected = Hex(HmacSha256(secret, timestamp + apiKey + recvWindow + query));

        CryptoAuth.BybitSignature(timestamp, apiKey, recvWindow, query, secret).Should().Be(expected);
    }

    [Fact]
    public void BybitIsLowercaseHex()
    {
        var signature = CryptoAuth.BybitSignature("1", "k", "5000", "a=1", "secret");

        signature.Should().MatchRegex("^[0-9a-f]+$");
        signature.Should().HaveLength(64);
    }

    [Fact]
    public void SwappingTheKeyAndTimestampChangesTheSignature()
    {
        CryptoAuth.BybitSignature("1658384314791", "KEY", "5000", "", "s")
            .Should().NotBe(CryptoAuth.BybitSignature("KEY", "1658384314791", "5000", "", "s"));
    }

    // ── OKX ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OkxSignsTimestampMethodPathAndBody()
    {
        // The documented pre-hash: 2020-12-08T09:08:57.715Z + GET + /api/v5/account/balance?ccy=BTC
        const string secret = "22582BD0CFF14C41EDBF1AB98506286D";
        const string timestamp = "2020-12-08T09:08:57.715Z";
        const string path = "/api/v5/account/balance?ccy=BTC";

        var expected = Convert.ToBase64String(HmacSha256(secret, timestamp + "GET" + path));

        CryptoAuth.OkxSignature(timestamp, "GET", path, string.Empty, secret).Should().Be(expected);
    }

    [Fact]
    public void OkxWantsIsoMillisecondsNotUnixTime()
    {
        // Sending Unix milliseconds here is rejected with nothing to distinguish it from a bad key.
        var timestamp = CryptoAuth.OkxTimestamp(
            new DateTimeOffset(2020, 12, 8, 9, 8, 57, 715, TimeSpan.Zero));

        timestamp.Should().Be("2020-12-08T09:08:57.715Z");
    }

    [Fact]
    public void OkxIncludesTheQueryStringInTheSignedPath()
    {
        CryptoAuth.OkxSignature("t", "GET", "/api/v5/x?ccy=BTC", "", "s")
            .Should().NotBe(CryptoAuth.OkxSignature("t", "GET", "/api/v5/x", "", "s"));
    }

    [Fact]
    public void OkxIsBase64NotHex()
    {
        var signature = CryptoAuth.OkxSignature("t", "GET", "/api/v5/x", "", "secret");

        var decode = () => Convert.FromBase64String(signature);
        decode.Should().NotThrow();
    }

    // ── Kraken ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void KrakenMatchesThePublishedVector()
    {
        // The full documented example. Every step has to be right for this to land — the raw digest
        // rather than its hex form, and the base64-decoded secret rather than its text.
        const string secret =
            "kQH5HW/8p1uGOVjbgWA7FunAmGO8lsSUXNsu3eow76sz84Q18fWxnyRzBHCd3pd5nE9qa99HAZtuZuj6F1huXg==";
        const string nonce = "1616492376594";
        const string postData =
            "nonce=1616492376594&ordertype=limit&pair=XBTUSD&price=37500&type=buy&volume=1.25";

        CryptoAuth.KrakenSignature("/0/private/AddOrder", nonce, postData, secret)
            .Should().Be("4/dpxb3iT4tp/ZCVEwSnEsLxx0bqyhLpdfOpc6fn7OR8+UClSV5n9E6aSS8MPtnRfp32bAb0nmbRn6H8ndwLUQ==");
    }

    [Fact]
    public void KrakenBindsTheSignatureToThePath()
    {
        // The path is inside the signed message, so a signature for one endpoint cannot be replayed
        // against another.
        const string secret = "a2V5";
        const string nonce = "1";
        const string body = "nonce=1";

        CryptoAuth.KrakenSignature("/0/private/AddOrder", nonce, body, secret)
            .Should().NotBe(CryptoAuth.KrakenSignature("/0/private/Balance", nonce, body, secret));
    }

    [Fact]
    public void KrakenNoncesIncrease()
    {
        // The venue rejects any nonce it has already seen for that key, so a repeat locks you out until
        // you move past it.
        var earlier = CryptoAuth.KrakenNonce(DateTimeOffset.UnixEpoch.AddSeconds(1));
        var later = CryptoAuth.KrakenNonce(DateTimeOffset.UnixEpoch.AddSeconds(2));

        long.Parse(later).Should().BeGreaterThan(long.Parse(earlier));
    }

    // ── Coinbase ────────────────────────────────────────────────────────────────────────────────

    private static string PrivateKeyPem()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportECPrivateKeyPem();
    }

    [Fact]
    public void CoinbaseMintsAThreePartToken()
    {
        var token = CryptoAuth.CoinbaseJwt(
            "organizations/x/apiKeys/y", PrivateKeyPem(), "GET", "api.coinbase.com",
            "/api/v3/brokerage/products", DateTimeOffset.UtcNow);

        token.Split('.').Should().HaveCount(3);
    }

    [Fact]
    public void CoinbaseTokenVerifiesAgainstItsOwnKey()
    {
        // ECDSA is randomised, so there is no fixed expected value to compare against. What matters is
        // that the signature verifies — and that it is the IeeeP1363 pair JWS wants rather than the DER
        // form ECDsa produces by default, which is silently rejected as a malformed token.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportECPrivateKeyPem();

        var token = CryptoAuth.CoinbaseJwt(
            "key", pem, "GET", "api.coinbase.com", "/api/v3/brokerage/products", DateTimeOffset.UtcNow);

        var parts = token.Split('.');
        var signed = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");

        key.VerifyData(signed, FromBase64Url(parts[2]), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation).Should().BeTrue();
    }

    [Fact]
    public void CoinbaseTokenNamesTheExactRequestItAuthorises()
    {
        // The uri claim binds the token to one method and path, so a leaked token is not a general key.
        var token = CryptoAuth.CoinbaseJwt(
            "key", PrivateKeyPem(), "get", "api.coinbase.com", "/api/v3/brokerage/products",
            DateTimeOffset.UtcNow);

        using var payload = JsonDocument.Parse(FromBase64Url(token.Split('.')[1]));

        payload.RootElement.GetProperty("uri").GetString()
            .Should().Be("GET api.coinbase.com/api/v3/brokerage/products");
    }

    [Fact]
    public void CoinbaseTokenExpiresInTwoMinutes()
    {
        var issued = DateTimeOffset.UtcNow;
        var token = CryptoAuth.CoinbaseJwt(
            "key", PrivateKeyPem(), "GET", "h", "/p", issued);

        using var payload = JsonDocument.Parse(FromBase64Url(token.Split('.')[1]));

        (payload.RootElement.GetProperty("exp").GetInt64()
         - payload.RootElement.GetProperty("nbf").GetInt64()).Should().Be(120);
    }

    [Fact]
    public void EveryCoinbaseTokenCarriesItsOwnNonce()
    {
        // A repeated nonce is a replay. Two tokens minted in the same tick must still differ.
        var pem = PrivateKeyPem();
        var first = CryptoAuth.CoinbaseJwt("key", pem, "GET", "h", "/p", DateTimeOffset.UnixEpoch);
        var second = CryptoAuth.CoinbaseJwt("key", pem, "GET", "h", "/p", DateTimeOffset.UnixEpoch);

        first.Split('.')[0].Should().NotBe(second.Split('.')[0]);
    }

    [Fact]
    public void CoinbaseDeclaresES256()
    {
        var token = CryptoAuth.CoinbaseJwt("key", PrivateKeyPem(), "GET", "h", "/p", DateTimeOffset.UtcNow);

        using var header = JsonDocument.Parse(FromBase64Url(token.Split('.')[0]));

        header.RootElement.GetProperty("alg").GetString().Should().Be("ES256");
        header.RootElement.GetProperty("typ").GetString().Should().Be("JWT");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static byte[] HmacSha256(string secret, string message)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
