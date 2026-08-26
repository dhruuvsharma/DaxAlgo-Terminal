using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.Infrastructure.Crypto;

/// <summary>The outcome of asking a venue whether a key works.</summary>
/// <param name="Ok">True when the venue answered the authenticated call.</param>
/// <param name="Detail">What to tell the user. Empty when <paramref name="Ok"/>.</param>
public readonly record struct ProbeResult(bool Ok, string Detail = "")
{
    public static ProbeResult Good { get; } = new(true);

    public static ProbeResult Bad(string detail) => new(false, detail);
}

/// <summary>
/// Asks a crypto venue "is this key good?" by making one signed, read-only account call.
///
/// <para><b>Why this exists at all.</b> Public market data on all six of these venues is keyless, so a
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
            using var request = Build(broker, credential, now);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return Read(broker, (int)response.StatusCode, body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ProbeResult.Bad(ex.Message);
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
                // a token comes back the key is good, which makes this the cheapest probe of the six.
                var url = "https://www.deribit.com/api/v2/public/auth"
                    + "?grant_type=client_credentials"
                    + $"&client_id={Uri.EscapeDataString(credential.Key)}"
                    + $"&client_secret={Uri.EscapeDataString(credential.Secret)}";
                return new HttpRequestMessage(HttpMethod.Get, url);
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
    internal static ProbeResult Read(BrokerKind broker, int status, string? body)
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
        }

        if (status is >= 200 and < 300) return ProbeResult.Good;

        // Binance, Bybit and Coinbase answer a bad key with a status and a message worth repeating —
        // "signature for this request is not valid" is far more actionable than "401".
        var detail = Text(body, "msg") ?? Text(body, "message") ?? Text(body, "retMsg");
        return ProbeResult.Bad(
            string.IsNullOrWhiteSpace(detail) ? $"HTTP {status}." : $"{detail} (HTTP {status}).");
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
