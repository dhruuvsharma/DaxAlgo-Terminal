using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradingTerminal.Infrastructure.Crypto;

/// <summary>
/// Request signing for the keyed crypto venues.
///
/// <para>Five venues, five schemes, no two alike — and each one is unforgiving: a signature that is
/// wrong in any detail is rejected identically to a stolen key, so there is no partial credit and no
/// useful error message to debug against. Every method here is built from the venue's published
/// algorithm, and the tests check them against the worked examples those same documents publish.</para>
///
/// <para><b>This is verifiable without an account, which is why it is worth doing properly now.</b>
/// A signature is a pure function of key, time and request. Three of the five venues publish a vector
/// with a known secret and an expected output, so the code can be proved correct today rather than
/// discovered wrong later against someone's real key.</para>
///
/// <para><b>Public market data needs none of this.</b> All five serve quotes, books and candles with no
/// credentials at all. What a key buys is a higher rate-limit budget and, later, the private endpoints
/// that order routing will need — so this is the foundation for those, not a market-data feature.</para>
/// </summary>
public static class CryptoAuth
{
    /// <summary>Milliseconds since the Unix epoch — the timestamp four of the five venues want.</summary>
    public static string UnixMilliseconds(DateTimeOffset at) =>
        at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    // ── Binance ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Binance: HMAC-SHA256 over the query string concatenated with the body, hex-encoded, sent as a
    /// <c>signature</c> query parameter alongside an <c>X-MBX-APIKEY</c> header.
    /// </summary>
    /// <param name="query">Everything that will be sent, already in <c>a=1&amp;b=2</c> form and
    /// including <c>timestamp</c>. Signed exactly as it will be transmitted — re-ordering after signing
    /// is the classic way to produce a valid signature for a different request.</param>
    /// <param name="secret">The API secret, used as raw UTF-8 bytes.</param>
    public static string BinanceSignature(string query, string secret)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(secret);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(query)));
    }

    // ── Bybit ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bybit v5: HMAC-SHA256 over <c>timestamp + apiKey + recvWindow + payload</c>, lowercase hex.
    ///
    /// <para>The payload is the query string for a GET and the JSON body for a POST. The order of the
    /// four parts is fixed and unlabelled in the signed string, so a swapped key and timestamp produce
    /// a perfectly well-formed signature that is simply wrong.</para>
    /// </summary>
    public static string BybitSignature(
        string timestamp, string apiKey, string recvWindow, string payload, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var message = string.Concat(timestamp, apiKey, recvWindow, payload);
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));
    }

    // ── OKX ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// OKX v5: base64 HMAC-SHA256 over <c>timestamp + METHOD + requestPath + body</c>.
    ///
    /// <para>Two details that are easy to get wrong and impossible to diagnose from the rejection: the
    /// timestamp is ISO 8601 with exactly millisecond precision rather than Unix milliseconds, and the
    /// request path includes the query string. OKX also wants a passphrase header, which is chosen when
    /// the key is created and is not the account password.</para>
    /// </summary>
    /// <param name="timestamp">ISO 8601 UTC with milliseconds — see <see cref="OkxTimestamp"/>.</param>
    /// <param name="method">Upper-case HTTP method.</param>
    /// <param name="requestPath">Path including any query string, starting with <c>/api/v5/</c>.</param>
    /// <param name="body">JSON body, or empty for a GET.</param>
    public static string OkxSignature(
        string timestamp, string method, string requestPath, string body, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var message = string.Concat(timestamp, method.ToUpperInvariant(), requestPath, body ?? string.Empty);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));
    }

    /// <summary>OKX's timestamp format: ISO 8601, UTC, milliseconds, trailing Z. Not Unix time.</summary>
    public static string OkxTimestamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    // ── Kraken ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Kraken: the most involved of the five, and the one where every step matters.
    ///
    /// <para><c>base64(HMAC-SHA512(uriPath || SHA256(nonce || postData), base64decode(secret)))</c>.</para>
    ///
    /// <list type="number">
    ///   <item>SHA-256 the nonce concatenated with the URL-encoded POST data. The nonce is concatenated
    ///     as text and <b>also</b> appears inside the POST data; both are required.</item>
    ///   <item>Concatenate the URI path's bytes with the raw digest bytes — the digest is not
    ///     hex-encoded first, which is the step most implementations get wrong.</item>
    ///   <item>HMAC-SHA512 with the <b>base64-decoded</b> secret. Kraken's secret is base64 and using
    ///     its text form produces a valid-looking signature that is always rejected.</item>
    /// </list>
    /// </summary>
    /// <param name="uriPath">The private path, for example <c>/0/private/AddOrder</c>.</param>
    /// <param name="nonce">The nonce, which must also be present in <paramref name="postData"/>.</param>
    /// <param name="postData">The URL-encoded body exactly as it will be sent.</param>
    /// <param name="secretBase64">The API secret, base64 as the venue issues it.</param>
    public static string KrakenSignature(
        string uriPath, string nonce, string postData, string secretBase64)
    {
        ArgumentNullException.ThrowIfNull(uriPath);
        ArgumentNullException.ThrowIfNull(secretBase64);

        var inner = SHA256.HashData(Encoding.UTF8.GetBytes(nonce + postData));

        var pathBytes = Encoding.UTF8.GetBytes(uriPath);
        var message = new byte[pathBytes.Length + inner.Length];
        Buffer.BlockCopy(pathBytes, 0, message, 0, pathBytes.Length);
        Buffer.BlockCopy(inner, 0, message, pathBytes.Length, inner.Length);

        using var hmac = new HMACSHA512(Convert.FromBase64String(secretBase64));
        return Convert.ToBase64String(hmac.ComputeHash(message));
    }

    /// <summary>A Kraken nonce: always increasing, because the venue rejects any value it has seen
    /// before for that key. Milliseconds is the conventional choice.</summary>
    public static string KrakenNonce(DateTimeOffset at) => UnixMilliseconds(at);

    // ── Coinbase ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Coinbase: a short-lived ES256 JWT, not an HMAC — the one scheme here that is asymmetric.
    ///
    /// <para>The key is an EC P-256 private key in PEM, and the token carries a URI claim naming the
    /// exact request it authorises, so a token is not reusable across endpoints. It expires two minutes
    /// after issue, which means minting per request rather than caching one.</para>
    ///
    /// <para>ECDSA signatures are randomised, so the same input signs differently every time. That rules
    /// out a fixed expected value in a test — what is checked instead is that the token verifies against
    /// its own public key, which is the property that actually matters.</para>
    /// </summary>
    /// <param name="keyName">The CDP key name, used as both issuer subject and key id.</param>
    /// <param name="privateKeyPem">The EC private key, PEM encoded.</param>
    /// <param name="method">Upper-case HTTP method.</param>
    /// <param name="host">Host without scheme, for example <c>api.coinbase.com</c>.</param>
    /// <param name="path">Request path.</param>
    /// <param name="issuedAt">When the token is minted.</param>
    public static string CoinbaseJwt(
        string keyName, string privateKeyPem, string method, string host, string path,
        DateTimeOffset issuedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);

        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);

        var issued = issuedAt.ToUnixTimeSeconds();

        // A per-token nonce, because the header carries one and a repeated value is a replay.
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["typ"] = "JWT",
            ["alg"] = "ES256",
            ["kid"] = keyName,
            ["nonce"] = nonce,
        }));

        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["sub"] = keyName,
            ["iss"] = "cdp",
            ["nbf"] = issued,
            // Two minutes, as documented. Longer would be a token worth stealing.
            ["exp"] = issued + 120,
            ["uri"] = $"{method.ToUpperInvariant()} {host}{path}",
        }));

        var signingInput = $"{header}.{payload}";

        // IeeeP1363 gives the fixed-width r||s pair JWS requires. The DER encoding ECDsa produces by
        // default is a different shape and is rejected as a malformed token.
        var signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"{signingInput}.{Base64Url(signature)}";
    }

    /// <summary>Base64url without padding, as JWS requires.</summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
