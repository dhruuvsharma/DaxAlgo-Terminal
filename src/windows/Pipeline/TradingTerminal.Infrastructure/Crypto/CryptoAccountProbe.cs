using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.Infrastructure.Crypto;

/// <summary>The outcome of asking a venue whether a key works.</summary>
/// <param name="Ok">True when the venue answered the authenticated call.</param>
/// <param name="Detail">What to tell the user. Empty when <paramref name="Ok"/>.</param>
/// <param name="Reached">False when no verdict was obtained at all — the host was unreachable, timed
/// out, rate-limited the call, or answered with an error page rather than the venue's own words. Such a
/// result is <b>not a refusal</b>: telling someone with a good key to regenerate it is worse than not
/// checking. Before this existed every transport failure came back as <see cref="Bad"/>, and the login
/// form rejected good keys on a slow network.</param>
public readonly record struct ProbeResult(bool Ok, string Detail = "", bool Reached = true)
{
    public static ProbeResult Good { get; } = new(true);

    public static ProbeResult Bad(string detail) => new(false, detail);

    public static ProbeResult Unreachable(string detail) => new(false, detail, Reached: false);
}

/// <summary>
/// Asks a crypto venue "is this key good?" by making one signed, read-only account call.
///
/// <para><b>Why this exists at all.</b> Public market data on every one of these venues is keyless, so a
/// key that is wrong — or right but pasted into the wrong venue's form, or missing its passphrase —
/// changes nothing a user can see. Charts keep drawing from the public feed and the key sits there
/// looking configured. The probe turns that into an answer at the moment the key is pasted, which is
/// the only moment the user still has the key on their clipboard and the API page open.</para>
///
/// <para><b>It reads a balance and nothing else.</b> The lightest authenticated endpoint each venue
/// offers, chosen so that a probe can never move money even if the code is wrong. A key restricted to
/// read-only — which is what these forms tell users to create — passes.</para>
///
/// <para><b>The trap that makes a naive probe useless:</b> Kraken answers a rejected key with
/// <b>HTTP 200</b> and an <c>error</c> array in the body, and OKX answers with HTTP 200 and a non-zero
/// <c>code</c>. A probe that trusts the status line reports every bad key as good. Each venue's body is
/// read.</para>
/// </summary>
public static class CryptoAccountProbe
{
    /// <summary>True when this venue has a keyed mode worth probing.</summary>
    public static bool Supports(BrokerKind broker) => broker switch
    {
        BrokerKind.Binance or BrokerKind.Bybit or BrokerKind.Okx
            or BrokerKind.Kraken or BrokerKind.Coinbase or BrokerKind.Deribit => true,
        BrokerKind.Bitget or BrokerKind.KuCoin or BrokerKind.GateIo or BrokerKind.Gemini
            or BrokerKind.CryptoCom or BrokerKind.Upbit or BrokerKind.Bithumb or BrokerKind.Bitfinex
            or BrokerKind.Bitstamp or BrokerKind.Bitvavo or BrokerKind.Htx or BrokerKind.Mexc => true,
        _ => false,
    };

    /// <summary>
    /// Makes the call. Returns the venue's verdict, or a description of why one could not be obtained.
    /// <b>Never throws</b> — a probe that fails must not stop a keyless feed from connecting.
    /// </summary>
    public static async Task<ProbeResult> ProbeAsync(
        HttpClient http, BrokerKind broker, BrokerCredential credential,
        DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (!Supports(broker)) return ProbeResult.Bad($"{broker} has no keyed mode.");
        if (!credential.IsConfigured) return ProbeResult.Bad("No key stored.");

        try
        {
            using var response = await SendAsync(http, broker, credential, now, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return Read(broker, (int)response.StatusCode, body, response.ReasonPhrase);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // No answer is not a "no". This includes HttpClient's own timeout, which surfaces as a
            // cancellation the caller did not ask for.
            return ProbeResult.Unreachable(ex.Message);
        }
    }

    /// <summary>
    /// Sends the probe, once more if the connection itself failed. Some edges reset a share of TLS
    /// handshakes outright — Bitstamp's does, from some networks — and the next attempt succeeds. The
    /// request is rebuilt rather than re-sent: a sent <see cref="HttpRequestMessage"/> cannot be reused, and
    /// a fresh one carries a fresh nonce, which the venues that refuse a repeated nonce require anyway.
    /// </summary>
    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, BrokerKind broker, BrokerCredential credential, DateTimeOffset now, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = Build(broker, credential, now.AddMilliseconds(attempt - 1));
            try
            {
                return await http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null && attempt < 2 && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            }
        }
    }

    // ── request ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Builds one venue's signed balance request. Each is the documented scheme; the comments
    /// name the part that is easy to get wrong and impossible to diagnose from the rejection.</summary>
    internal static HttpRequestMessage Build(
        BrokerKind broker, BrokerCredential credential, DateTimeOffset now)
    {
        switch (broker)
        {
            case BrokerKind.Binance:
            {
                // The signature covers the query string exactly as sent, so the signed text and the
                // transmitted text are built once and reused — rebuilding it re-orders nothing today
                // and breaks silently the day a parameter is added.
                var query = $"timestamp={CryptoAuth.UnixMilliseconds(now)}&recvWindow=5000";
                var signed = $"{query}&signature={CryptoAuth.BinanceSignature(query, credential.Secret)}";

                var request = new HttpRequestMessage(
                    HttpMethod.Get, $"https://api.binance.com/api/v3/account?{signed}");
                request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", credential.Key);
                return request;
            }

            case BrokerKind.Bybit:
            {
                const string Window = "5000";
                const string Query = "accountType=UNIFIED";
                var stamp = CryptoAuth.UnixMilliseconds(now);

                var request = new HttpRequestMessage(
                    HttpMethod.Get, $"https://api.bybit.com/v5/account/wallet-balance?{Query}");
                request.Headers.TryAddWithoutValidation("X-BAPI-API-KEY", credential.Key);
                request.Headers.TryAddWithoutValidation("X-BAPI-TIMESTAMP", stamp);
                request.Headers.TryAddWithoutValidation("X-BAPI-RECV-WINDOW", Window);
                request.Headers.TryAddWithoutValidation(
                    "X-BAPI-SIGN",
                    CryptoAuth.BybitSignature(stamp, credential.Key, Window, Query, credential.Secret));
                return request;
            }

            case BrokerKind.Okx:
            {
                // The signed path includes the query string, and the timestamp is ISO 8601 rather than
                // Unix milliseconds. Both are silent when wrong: the venue just says "invalid sign".
                const string Path = "/api/v5/account/balance";
                var stamp = CryptoAuth.OkxTimestamp(now);

                var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.okx.com{Path}");
                request.Headers.TryAddWithoutValidation("OK-ACCESS-KEY", credential.Key);
                request.Headers.TryAddWithoutValidation("OK-ACCESS-TIMESTAMP", stamp);
                request.Headers.TryAddWithoutValidation("OK-ACCESS-PASSPHRASE", credential.Passphrase);
                request.Headers.TryAddWithoutValidation(
                    "OK-ACCESS-SIGN",
                    CryptoAuth.OkxSignature(stamp, "GET", Path, string.Empty, credential.Secret));
                return request;
            }

            case BrokerKind.Kraken:
            {
                // The nonce appears twice — signed, and posted — and must be the same value. Kraken
                // also rejects any nonce it has already seen for that key, so two probes in the same
                // millisecond fail for a reason that has nothing to do with the key.
                const string Path = "/0/private/Balance";
                var nonce = CryptoAuth.KrakenNonce(now);
                var form = $"nonce={nonce}";

                var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.kraken.com{Path}")
                {
                    Content = new StringContent(
                        form, Encoding.UTF8, "application/x-www-form-urlencoded"),
                };
                request.Headers.TryAddWithoutValidation("API-Key", credential.Key);
                request.Headers.TryAddWithoutValidation(
                    "API-Sign", CryptoAuth.KrakenSignature(Path, nonce, form, credential.Secret));
                return request;
            }

            case BrokerKind.Coinbase:
            {
                // The JWT is bound to the method, host and path it authorises, and expires in two
                // minutes — so it is minted per request rather than cached.
                const string Host = "api.coinbase.com";
                const string Path = "/api/v3/brokerage/accounts";

                var request = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}{Path}");
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    CryptoAuth.CoinbaseJwt(
                        credential.Key, credential.Secret, "GET", Host, Path, now));
                return request;
            }

            case BrokerKind.Deribit:
            {
                // Deribit is the odd one out: no signature, an OAuth2 client-credentials exchange. If
                // a token comes back the key is good, which makes this the cheapest probe of them all.
                var url = "https://www.deribit.com/api/v2/public/auth"
                    + "?grant_type=client_credentials"
                    + $"&client_id={Uri.EscapeDataString(credential.Key)}"
                    + $"&client_secret={Uri.EscapeDataString(credential.Secret)}";
                return new HttpRequestMessage(HttpMethod.Get, url);
            }

            // ── The twelve added on 2026-09-25. Each reads a balance and nothing else. ─────────────

            case BrokerKind.Bitget:
            {
                const string Path = "/api/v2/spot/account/assets";
                var stamp = CryptoAuth.UnixMilliseconds(now);
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.bitget.com{Path}");
                request.Headers.TryAddWithoutValidation("ACCESS-KEY", credential.Key);
                request.Headers.TryAddWithoutValidation("ACCESS-TIMESTAMP", stamp);
                request.Headers.TryAddWithoutValidation("ACCESS-PASSPHRASE", credential.Passphrase);
                request.Headers.TryAddWithoutValidation("ACCESS-SIGN", CryptoAuth.BitgetSignature(stamp, "GET", Path, string.Empty, credential.Secret));
                request.Headers.TryAddWithoutValidation("locale", "en-US");
                return request;
            }

            case BrokerKind.KuCoin:
            {
                const string Path = "/api/v1/accounts";
                var stamp = CryptoAuth.UnixMilliseconds(now);
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.kucoin.com{Path}");
                request.Headers.TryAddWithoutValidation("KC-API-KEY", credential.Key);
                request.Headers.TryAddWithoutValidation("KC-API-TIMESTAMP", stamp);
                request.Headers.TryAddWithoutValidation("KC-API-SIGN", CryptoAuth.KuCoinSignature(stamp, "GET", Path, string.Empty, credential.Secret));
                // Version 2 keys: the passphrase travels signed, not in clear.
                request.Headers.TryAddWithoutValidation("KC-API-PASSPHRASE", CryptoAuth.KuCoinPassphrase(credential.Passphrase, credential.Secret));
                request.Headers.TryAddWithoutValidation("KC-API-KEY-VERSION", "2");
                return request;
            }

            case BrokerKind.GateIo:
            {
                const string Path = "/api/v4/spot/accounts";
                var seconds = now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.gateio.ws{Path}");
                request.Headers.TryAddWithoutValidation("KEY", credential.Key);
                request.Headers.TryAddWithoutValidation("Timestamp", seconds);
                request.Headers.TryAddWithoutValidation("SIGN", CryptoAuth.GateIoSignature("GET", Path, string.Empty, string.Empty, seconds, credential.Secret));
                request.Headers.Accept.ParseAdd("application/json");
                return request;
            }

            case BrokerKind.Gemini:
            {
                // The whole request rides in a header; the POST body is empty.
                const string Path = "/v1/balances";
                var (payload, signature) = CryptoAuth.GeminiSignature(
                    $"{{\"request\":\"{Path}\",\"nonce\":{CryptoAuth.UnixMilliseconds(now)}}}", credential.Secret);
                var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.gemini.com{Path}")
                {
                    Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain"),
                };
                request.Headers.TryAddWithoutValidation("X-GEMINI-APIKEY", credential.Key);
                request.Headers.TryAddWithoutValidation("X-GEMINI-PAYLOAD", payload);
                request.Headers.TryAddWithoutValidation("X-GEMINI-SIGNATURE", signature);
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                return request;
            }

            case BrokerKind.CryptoCom:
            {
                const string Method = "private/user-balance";
                const string Id = "1";
                var nonce = CryptoAuth.UnixMilliseconds(now);
                var sig = CryptoAuth.CryptoComSignature(Method, Id, credential.Key, string.Empty, nonce, credential.Secret);
                var body = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["id"] = 1, ["method"] = Method, ["api_key"] = credential.Key,
                    ["params"] = new Dictionary<string, object>(), ["nonce"] = long.Parse(nonce, System.Globalization.CultureInfo.InvariantCulture),
                    ["sig"] = sig,
                });
                return new HttpRequestMessage(HttpMethod.Post, $"https://api.crypto.com/exchange/v1/{Method}")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }

            case BrokerKind.Upbit:
            {
                var token = CryptoAuth.JwtHs256(
                [
                    new("access_key", credential.Key),
                    new("nonce", Guid.NewGuid().ToString()),
                ], credential.Secret);
                var request = new HttpRequestMessage(HttpMethod.Get, "https://api.upbit.com/v1/accounts");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return request;
            }

            case BrokerKind.Bithumb:
            {
                // Bithumb's JWT carries a millisecond timestamp as well as Upbit's two claims.
                var token = CryptoAuth.JwtHs256(
                [
                    new("access_key", credential.Key),
                    new("nonce", Guid.NewGuid().ToString()),
                    new("timestamp", now.ToUnixTimeMilliseconds()),
                ], credential.Secret);
                var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bithumb.com/v1/accounts");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return request;
            }

            case BrokerKind.Bitfinex:
            {
                const string Path = "v2/auth/r/wallets";
                const string Body = "{}";
                // Microseconds: the nonce must rise with every call on a key, and milliseconds collide
                // when two terminals share one.
                var nonce = (now.ToUnixTimeMilliseconds() * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.bitfinex.com/{Path}")
                {
                    Content = new StringContent(Body, Encoding.UTF8, "application/json"),
                };
                request.Headers.TryAddWithoutValidation("bfx-nonce", nonce);
                request.Headers.TryAddWithoutValidation("bfx-apikey", credential.Key);
                request.Headers.TryAddWithoutValidation("bfx-signature", CryptoAuth.BitfinexSignature(Path, nonce, Body, credential.Secret));
                return request;
            }

            case BrokerKind.Bitstamp:
            {
                const string Host = "www.bitstamp.net";
                const string Path = "/api/v2/account_balances/";
                var nonce = Guid.NewGuid().ToString();
                var stamp = CryptoAuth.UnixMilliseconds(now);
                // No body, so no content type — neither signed nor sent.
                var request = new HttpRequestMessage(HttpMethod.Post, $"https://{Host}{Path}");
                request.Headers.TryAddWithoutValidation("X-Auth", "BITSTAMP " + credential.Key);
                request.Headers.TryAddWithoutValidation("X-Auth-Signature", CryptoAuth.BitstampSignature(
                    credential.Key, "POST", Host, Path, string.Empty, string.Empty, nonce, stamp, string.Empty, credential.Secret));
                request.Headers.TryAddWithoutValidation("X-Auth-Nonce", nonce);
                request.Headers.TryAddWithoutValidation("X-Auth-Timestamp", stamp);
                request.Headers.TryAddWithoutValidation("X-Auth-Version", "v2");
                return request;
            }

            case BrokerKind.Bitvavo:
            {
                const string Path = "/v2/balance";
                var stamp = CryptoAuth.UnixMilliseconds(now);
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.bitvavo.com{Path}");
                request.Headers.TryAddWithoutValidation("Bitvavo-Access-Key", credential.Key);
                request.Headers.TryAddWithoutValidation("Bitvavo-Access-Timestamp", stamp);
                request.Headers.TryAddWithoutValidation("Bitvavo-Access-Signature", CryptoAuth.BitvavoSignature(stamp, "GET", Path, string.Empty, credential.Secret));
                request.Headers.TryAddWithoutValidation("Bitvavo-Access-Window", "10000");
                return request;
            }

            case BrokerKind.Htx:
            {
                const string Host = "api.huobi.pro";
                const string Path = "/v1/account/accounts";
                var query = CryptoAuth.HtxQuery(
                [
                    new("AccessKeyId", credential.Key),
                    new("SignatureMethod", "HmacSHA256"),
                    new("SignatureVersion", "2"),
                    new("Timestamp", CryptoAuth.HtxTimestamp(now)),
                ]);
                var signature = CryptoAuth.HtxSignature("GET", Host, Path, query, credential.Secret);
                return new HttpRequestMessage(HttpMethod.Get,
                    $"https://{Host}{Path}?{query}&Signature={Uri.EscapeDataString(signature)}");
            }

            case BrokerKind.Mexc:
            {
                var query = $"timestamp={CryptoAuth.UnixMilliseconds(now)}&recvWindow=5000";
                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.mexc.com/api/v3/account?{query}&signature={CryptoAuth.MexcSignature(query, credential.Secret)}");
                request.Headers.TryAddWithoutValidation("X-MEXC-APIKEY", credential.Key);
                return request;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(broker), broker, "No keyed mode.");
        }
    }

    // ── response ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a venue's verdict out of its body.
    ///
    /// <para>Split from the request so it can be tested against captured payloads without a key: these
    /// are exactly the responses a wrong key produces, and they are the ones a naive probe misreads.</para>
    /// </summary>
    /// <param name="reasonPhrase">The status line's text. Bybit puts its whole explanation there —
    /// "401 API key is invalid." — and sends an empty body.</param>
    internal static ProbeResult Read(BrokerKind broker, int status, string? body, string? reasonPhrase = null)
    {
        body ??= string.Empty;

        // Kraken and OKX report rejection inside a 200, and Deribit inside a JSON-RPC error object.
        // Their bodies are read before the status line is trusted.
        switch (broker)
        {
            case BrokerKind.Kraken:
            {
                var error = First(body, "error");
                return string.IsNullOrEmpty(error) ? ProbeResult.Good : ProbeResult.Bad(error);
            }

            case BrokerKind.Okx:
            {
                var code = Text(body, "code");
                if (code is null or "0") break;
                return ProbeResult.Bad($"{Text(body, "msg") ?? "rejected"} (code {code})");
            }

            case BrokerKind.Deribit:
            {
                var message = Nested(body, "error", "message");
                if (message is not null) return ProbeResult.Bad(message);
                break;
            }

            // ── The twelve added on 2026-09-25 ───────────────────────────────────────────────────

            case BrokerKind.Bitget:
            {
                // Success is the string "00000", not zero.
                var code = Text(body, "code");
                if (code is null or "00000") break;
                return ProbeResult.Bad($"{Text(body, "msg") ?? "rejected"} (code {code})");
            }

            case BrokerKind.KuCoin:
            {
                var code = Text(body, "code");
                if (code is null or "200000") break;
                return ProbeResult.Bad($"{Text(body, "msg") ?? "rejected"} (code {code})");
            }

            case BrokerKind.GateIo:
            {
                var label = Text(body, "label");
                if (label is null) break;
                return ProbeResult.Bad($"{Text(body, "message") ?? label} ({label})");
            }

            case BrokerKind.Gemini:
            {
                if (Text(body, "result") != "error") break;
                var reason = Text(body, "reason");
                return ProbeResult.Bad($"{Text(body, "message") ?? reason ?? "rejected"} ({reason})");
            }

            case BrokerKind.CryptoCom:
            {
                var code = Text(body, "code");
                if (code is null or "0") break;
                return ProbeResult.Bad($"{Text(body, "message") ?? "rejected"} (code {code})");
            }

            case BrokerKind.Upbit or BrokerKind.Bithumb:
            {
                // Often only a name — {"error":{"name":"invalid_access_key"}} — with no message at all.
                var name = Nested(body, "error", "name");
                var message = Nested(body, "error", "message");
                if (name is null && message is null) break;
                return ProbeResult.Bad(message is null ? name! : $"{message} ({name})");
            }

            case BrokerKind.Bitfinex:
            {
                // An error is an array — ["error", 10100, "apikey: invalid"] — and arrives as HTTP 500,
                // which the generic path below would otherwise take for the venue being down.
                if (BitfinexError(body) is { } error) return ProbeResult.Bad(error);
                break;
            }

            case BrokerKind.Bitstamp or BrokerKind.Htx:
            {
                // HTX reports a bad signature inside an HTTP 200, as {"status":"error","err-msg":…}.
                if (Text(body, "status") != "error") break;
                var reason = Text(body, "reason") ?? Text(body, "err-msg") ?? "rejected";
                var code = Text(body, "code") ?? Text(body, "err-code");
                return ProbeResult.Bad(code is null ? reason : $"{reason} ({code})");
            }

            case BrokerKind.Bitvavo:
            {
                var code = Text(body, "errorCode");
                if (code is null) break;
                return ProbeResult.Bad($"{Text(body, "error") ?? "rejected"} (error {code})");
            }
        }

        if (status is >= 200 and < 300) return ProbeResult.Good;

        // Binance, Bybit, Coinbase and MEXC answer a bad key with a status and a message worth
        // repeating — "signature for this request is not valid" is far more actionable than "401".
        var detail = Text(body, "msg") ?? Text(body, "message") ?? Text(body, "retMsg") ?? Informative(reasonPhrase);

        // Neither a server fault, nor rate-limiting, nor a page the venue did not write (an edge proxy's
        // HTML) says anything about the key. Those are "could not check", never "refused". A 401 is the
        // exception: whatever its body, "unauthorised" is an answer about the key.
        if (status >= 500 || status == 429 || (status != 401 && detail is null && !IsJson(body)))
            return ProbeResult.Unreachable(
                string.IsNullOrWhiteSpace(detail) ? $"HTTP {status}." : $"{detail} (HTTP {status}).");

        return ProbeResult.Bad(
            string.IsNullOrWhiteSpace(detail) ? $"HTTP {status}." : $"{detail} (HTTP {status}).");
    }

    /// <summary>A status line's reason phrase, when it says more than the status code already does.</summary>
    private static string? Informative(string? reasonPhrase) =>
        string.IsNullOrWhiteSpace(reasonPhrase)
        || reasonPhrase is "OK" or "Bad Request" or "Unauthorized" or "Forbidden" or "Not Found"
            or "Too Many Requests" or "Internal Server Error" or "Bad Gateway" or "Service Unavailable"
            ? null
            : reasonPhrase.Trim();

    private static bool IsJson(string body)
    {
        try
        {
            using var _ = JsonDocument.Parse(body);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Bitfinex's error array, as "message (code)", or null.</summary>
    private static string? BitfinexError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 3
                || root[0].ValueKind != JsonValueKind.String || root[0].GetString() != "error")
                return null;
            return $"{Scalar(root[2]) ?? "rejected"} (code {Scalar(root[1])})";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A top-level string property, or null. Numbers are read as text so a venue that changes
    /// <c>code</c> from <c>"1"</c> to <c>1</c> does not turn a rejection into a pass.</summary>
    private static string? Text(string body, string name) => Read(body, root =>
        root.TryGetProperty(name, out var value) ? Scalar(value) : null);

    /// <summary>A string one level down, for the JSON-RPC shape where the message is in an
    /// <c>error</c> object rather than at the top level.</summary>
    private static string? Nested(string body, string outer, string inner) => Read(body, root =>
        root.TryGetProperty(outer, out var nested) && nested.ValueKind == JsonValueKind.Object
        && nested.TryGetProperty(inner, out var value)
            ? Scalar(value)
            : null);

    /// <summary>The first entry of a top-level string array, or empty. Kraken's <c>error</c> is an
    /// array that is present and empty on success, so "absent" and "empty" both mean good.</summary>
    private static string First(string body, string name) => Read(body, root =>
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Array) return null;

        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String) return entry.GetString();
        }

        return null;
    }) ?? string.Empty;

    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };

    /// <summary>Parses and reads, treating unparseable bodies as "said nothing". A venue that answers
    /// with an HTML error page must not take the probe down — the status line still decides.</summary>
    private static string? Read(string body, Func<JsonElement, string?> read)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object ? read(json.RootElement) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
