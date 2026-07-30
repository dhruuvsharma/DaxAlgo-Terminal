using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Accounts;

internal sealed record GoogleIdentity(
    string Subject,
    string EmailAddress,
    string? DisplayName,
    DateTimeOffset TokenExpiresAtUtc,
    string IdToken)
{
    public override string ToString() => "GoogleIdentity { IdToken = [REDACTED] }";
}

internal interface IGoogleIdentityProvider
{
    Task<GoogleIdentity> AuthenticateAsync(CancellationToken ct = default);
}

internal interface IGoogleOAuthBrowser
{
    void Open(Uri authorizationUri);
}

internal interface IGoogleOAuthCallbackReceiverFactory
{
    IGoogleOAuthCallbackReceiver Create();
}

internal interface IGoogleOAuthCallbackReceiver : IAsyncDisposable
{
    Uri RedirectUri { get; }

    Task<GoogleOAuthCallback> WaitForCallbackAsync(CancellationToken ct);
}

internal readonly record struct GoogleOAuthCallback(
    string? Code,
    string? State,
    string? Error);

internal sealed class GoogleOAuthClient(
    GoogleAuthOptions options,
    HttpClient httpClient,
    TimeProvider timeProvider,
    IGoogleOAuthCallbackReceiverFactory? callbackReceiverFactory = null,
    IGoogleOAuthBrowser? browser = null)
    : IGoogleIdentityProvider
{
    internal static readonly Uri AuthorizationEndpoint =
        new("https://accounts.google.com/o/oauth2/v2/auth");
    internal static readonly Uri TokenEndpoint =
        new("https://oauth2.googleapis.com/token");
    internal static readonly Uri JwksEndpoint =
        new("https://www.googleapis.com/oauth2/v3/certs");
    internal const string ExpectedIssuer = "https://accounts.google.com";

    private readonly IGoogleOAuthCallbackReceiverFactory _callbackReceiverFactory =
        callbackReceiverFactory ?? LoopbackGoogleOAuthCallbackReceiverFactory.Instance;
    private readonly IGoogleOAuthBrowser _browser = browser ?? SystemGoogleOAuthBrowser.Instance;

    public async Task<GoogleIdentity> AuthenticateAsync(CancellationToken ct = default)
    {
        var clientId = options.ClientId?.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new AccountProviderUnavailableException(
                "Google sign-in is not configured for this build.");
        }

        try
        {
            await using var callbackReceiver = _callbackReceiverFactory.Create();
            var verifier = CreateRandomValue();
            var state = CreateRandomValue();
            var nonce = CreateRandomValue();
            var challenge = Base64UrlEncode(
                SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var authorizationUri = BuildAuthorizationUri(
                clientId,
                callbackReceiver.RedirectUri,
                challenge,
                state,
                nonce);

            _browser.Open(authorizationUri);
            var callback = await callbackReceiver.WaitForCallbackAsync(ct);
            if (!string.IsNullOrWhiteSpace(callback.Error))
            {
                throw new AccountProviderUnavailableException(
                    "Google sign-in was not completed. Retry.");
            }

            if (string.IsNullOrWhiteSpace(callback.Code) ||
                string.IsNullOrWhiteSpace(callback.State) ||
                !FixedTimeEquals(state, callback.State))
            {
                throw new AccountProviderUnavailableException(
                    "Google sign-in returned an invalid callback. Close the browser tab and retry.");
            }

            var idToken = await ExchangeCodeAsync(
                callback.Code,
                verifier,
                clientId,
                callbackReceiver.RedirectUri,
                ct);
            return await ValidateIdTokenAsync(idToken, clientId, nonce, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AccountProviderUnavailableException)
        {
            throw;
        }
        catch
        {
            // OAuth material and provider exception details are deliberately not surfaced or logged.
            throw new AccountProviderUnavailableException(
                "Google sign-in could not be verified. Check your connection and retry.");
        }
    }

    internal static Uri BuildAuthorizationUri(
        string clientId,
        Uri redirectUri,
        string codeChallenge,
        string state,
        string nonce)
    {
        KeyValuePair<string, string>[] parameters =
        [
            new("client_id", clientId),
            new("redirect_uri", redirectUri.AbsoluteUri),
            new("response_type", "code"),
            new("scope", "openid email profile"),
            new("code_challenge", codeChallenge),
            new("code_challenge_method", "S256"),
            new("access_type", "offline"),
            new("prompt", "consent"),
            new("state", state),
            new("nonce", nonce),
        ];
        return BuildUri(AuthorizationEndpoint, parameters);
    }

    private async Task<string> ExchangeCodeAsync(
        string code,
        string verifier,
        string clientId,
        Uri redirectUri,
        CancellationToken ct)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("code", code),
            new("client_id", clientId),
            new("redirect_uri", redirectUri.AbsoluteUri),
            new("grant_type", "authorization_code"),
            new("code_verifier", verifier),
        };
        if (!string.IsNullOrWhiteSpace(options.ClientSecret))
            parameters.Add(new("client_secret", options.ClientSecret.Trim()));

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(parameters),
        };
        using var response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new AccountProviderUnavailableException(
                "Google sign-in could not exchange the authorization response. Retry.");
        }

        var responseBytes = await response.Content.ReadAsByteArrayAsync(ct);
        try
        {
            using var document = JsonDocument.Parse(responseBytes);
            if (!document.RootElement.TryGetProperty("id_token", out var tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(tokenElement.GetString()))
            {
                throw new AccountProviderUnavailableException(
                    "Google sign-in did not return a verifiable identity. Retry.");
            }

            return tokenElement.GetString()!;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(responseBytes);
        }
    }

    private async Task<GoogleIdentity> ValidateIdTokenAsync(
        string idToken,
        string clientId,
        string expectedNonce,
        CancellationToken ct)
    {
        if (idToken.Length > 128 * 1024)
        {
            throw new AccountProviderUnavailableException(
                "Google sign-in returned an invalid identity token. Retry.");
        }

        var segments = idToken.Split('.');
        if (segments.Length != 3)
        {
            throw new AccountProviderUnavailableException(
                "Google sign-in returned an invalid identity token. Retry.");
        }

        byte[]? headerBytes = null;
        byte[]? payloadBytes = null;
        byte[]? signatureBytes = null;
        try
        {
            headerBytes = Base64UrlDecode(segments[0]);
            payloadBytes = Base64UrlDecode(segments[1]);
            signatureBytes = Base64UrlDecode(segments[2]);
            using var header = JsonDocument.Parse(headerBytes);
            using var payload = JsonDocument.Parse(payloadBytes);

            var algorithm = RequiredString(header.RootElement, "alg");
            var keyId = RequiredString(header.RootElement, "kid");
            if (!string.Equals(algorithm, "RS256", StringComparison.Ordinal))
            {
                throw new AccountProviderUnavailableException(
                    "Google sign-in returned an unsupported identity token. Retry.");
            }

            var signingInput = Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]);
            try
            {
                await VerifySignatureAsync(keyId, signingInput, signatureBytes, ct);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signingInput);
            }

            var issuer = RequiredString(payload.RootElement, "iss");
            if (!string.Equals(issuer, ExpectedIssuer, StringComparison.Ordinal))
                throw InvalidClaims();

            if (!AudienceMatches(payload.RootElement, clientId))
                throw InvalidClaims();

            if (!payload.RootElement.TryGetProperty("exp", out var expirationElement) ||
                !expirationElement.TryGetInt64(out var expirationSeconds) ||
                expirationSeconds <= timeProvider.GetUtcNow().ToUnixTimeSeconds())
            {
                throw InvalidClaims();
            }

            var nonce = RequiredString(payload.RootElement, "nonce");
            if (!FixedTimeEquals(expectedNonce, nonce))
                throw InvalidClaims();

            var subject = RequiredString(payload.RootElement, "sub");
            var email = RequiredString(payload.RootElement, "email");
            var displayName = OptionalString(payload.RootElement, "name");
            return new GoogleIdentity(
                subject,
                email,
                displayName,
                DateTimeOffset.FromUnixTimeSeconds(expirationSeconds),
                idToken);
        }
        catch (AccountProviderUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException)
        {
            throw new AccountProviderUnavailableException(
                "Google sign-in returned an invalid identity token. Retry.");
        }
        finally
        {
            if (headerBytes is not null) CryptographicOperations.ZeroMemory(headerBytes);
            if (payloadBytes is not null) CryptographicOperations.ZeroMemory(payloadBytes);
            if (signatureBytes is not null) CryptographicOperations.ZeroMemory(signatureBytes);
        }
    }

    private async Task VerifySignatureAsync(
        string keyId,
        byte[] signingInput,
        byte[] signature,
        CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(JwksEndpoint, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new AccountProviderUnavailableException(
                "Google sign-in keys are temporarily unavailable. Check your connection and retry.");
        }

        var jwksBytes = await response.Content.ReadAsByteArrayAsync(ct);
        try
        {
            using var document = JsonDocument.Parse(jwksBytes);
            if (!document.RootElement.TryGetProperty("keys", out var keys) ||
                keys.ValueKind != JsonValueKind.Array)
            {
                throw InvalidClaims();
            }

            foreach (var key in keys.EnumerateArray())
            {
                if (!string.Equals(OptionalString(key, "kid"), keyId, StringComparison.Ordinal) ||
                    !string.Equals(OptionalString(key, "kty"), "RSA", StringComparison.Ordinal))
                {
                    continue;
                }

                var declaredAlgorithm = OptionalString(key, "alg");
                if (declaredAlgorithm is not null &&
                    !string.Equals(declaredAlgorithm, "RS256", StringComparison.Ordinal))
                {
                    continue;
                }

                var modulus = Base64UrlDecode(RequiredString(key, "n"));
                var exponent = Base64UrlDecode(RequiredString(key, "e"));
                try
                {
                    using var rsa = RSA.Create();
                    rsa.ImportParameters(new RSAParameters
                    {
                        Modulus = modulus,
                        Exponent = exponent,
                    });
                    if (rsa.VerifyData(
                            signingInput,
                            signature,
                            HashAlgorithmName.SHA256,
                            RSASignaturePadding.Pkcs1))
                    {
                        return;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(modulus);
                    CryptographicOperations.ZeroMemory(exponent);
                }

                throw InvalidClaims();
            }

            throw InvalidClaims();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(jwksBytes);
        }
    }

    private static bool AudienceMatches(JsonElement payload, string clientId)
    {
        if (!payload.TryGetProperty("aud", out var audience)) return false;
        if (audience.ValueKind == JsonValueKind.String)
            return string.Equals(audience.GetString(), clientId, StringComparison.Ordinal);
        if (audience.ValueKind != JsonValueKind.Array) return false;

        using var enumerator = audience.EnumerateArray();
        if (!enumerator.MoveNext()) return false;
        var matches = enumerator.Current.ValueKind == JsonValueKind.String &&
                      string.Equals(enumerator.Current.GetString(), clientId, StringComparison.Ordinal);
        return matches && !enumerator.MoveNext();
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        var value = OptionalString(element, propertyName);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw InvalidClaims();
    }

    private static string? OptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static AccountProviderUnavailableException InvalidClaims() => new(
        "Google sign-in returned an invalid identity token. Retry.");

    private static Uri BuildUri(
        Uri endpoint,
        IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var query = string.Join(
            "&",
            parameters.Select(pair =>
                Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return new Uri(endpoint.AbsoluteUri + "?" + query);
    }

    private static string CreateRandomValue() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    internal static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    internal static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            0 => base64,
            2 => base64 + "==",
            3 => base64 + "=",
            _ => throw new FormatException("Invalid base64url value."),
        };
        return Convert.FromBase64String(base64);
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        try
        {
            return expectedBytes.Length == actualBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(actualBytes);
        }
    }
}

internal sealed class SystemGoogleOAuthBrowser : IGoogleOAuthBrowser
{
    public static SystemGoogleOAuthBrowser Instance { get; } = new();

    public void Open(Uri authorizationUri) =>
        Process.Start(new ProcessStartInfo(authorizationUri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
}

internal sealed class LoopbackGoogleOAuthCallbackReceiverFactory
    : IGoogleOAuthCallbackReceiverFactory
{
    public static LoopbackGoogleOAuthCallbackReceiverFactory Instance { get; } = new();

    public IGoogleOAuthCallbackReceiver Create() =>
        new LoopbackGoogleOAuthCallbackReceiver(ReserveEphemeralPort());

    private static int ReserveEphemeralPort()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        return ((IPEndPoint)reservation.LocalEndpoint).Port;
    }
}

internal sealed class LoopbackGoogleOAuthCallbackReceiver : IGoogleOAuthCallbackReceiver
{
    private const string CallbackPath = "/oidc/callback/";
    private readonly HttpListener _listener = new();
    private bool _disposed;

    public LoopbackGoogleOAuthCallbackReceiver(int port)
    {
        RedirectUri = new Uri("http://127.0.0.1:" + port + CallbackPath);
        _listener.Prefixes.Add(RedirectUri.AbsoluteUri);
        _listener.Start();
    }

    public Uri RedirectUri { get; }

    public async Task<GoogleOAuthCallback> WaitForCallbackAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var registration = ct.Register(static state =>
        {
            try
            {
                ((HttpListener)state!).Close();
            }
            catch
            {
            }
        }, _listener);

        HttpListenerContext context;
        try
        {
            context = await _listener.GetContextAsync();
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        var callback = ParseCallback(context.Request);
        try
        {
            await WriteBrowserResponseAsync(context.Response, callback, ct);
            return callback;
        }
        finally
        {
            try
            {
                _listener.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _listener.Close();
        return ValueTask.CompletedTask;
    }

    private static GoogleOAuthCallback ParseCallback(HttpListenerRequest request)
    {
        var requestUri = request.Url;
        if (!string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal) ||
            requestUri is null ||
            !string.Equals(requestUri.AbsolutePath, CallbackPath, StringComparison.Ordinal) ||
            request.RemoteEndPoint is not { Address: { } remoteAddress } ||
            !IPAddress.IsLoopback(remoteAddress))
        {
            return new GoogleOAuthCallback(null, null, "invalid_callback");
        }

        var query = ParseQuery(requestUri.Query);
        return new GoogleOAuthCallback(
            query.GetValueOrDefault("code"),
            query.GetValueOrDefault("state"),
            query.GetValueOrDefault("error"));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split(
                     '&',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            var value = parts.Length == 2
                ? Uri.UnescapeDataString(parts[1].Replace('+', ' '))
                : string.Empty;
            if (!values.TryAdd(key, value))
                return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return values;
    }

    private static async Task WriteBrowserResponseAsync(
        HttpListenerResponse response,
        GoogleOAuthCallback callback,
        CancellationToken ct)
    {
        var success = !string.IsNullOrWhiteSpace(callback.Code) &&
                      !string.IsNullOrWhiteSpace(callback.State) &&
                      string.IsNullOrWhiteSpace(callback.Error);
        var title = success ? "Sign-in received" : "Sign-in was not completed";
        var message = success
            ? "Return to DaxAlgo Terminal to finish account verification."
            : "Return to DaxAlgo Terminal and retry the sign-in.";
        var html =
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + title +
            "</title></head><body><h1>" + title + "</h1><p>" + message +
            "</p><p>You may close this tab.</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        response.StatusCode = success ? (int)HttpStatusCode.OK : (int)HttpStatusCode.BadRequest;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = body.Length;
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'";
        try
        {
            await response.OutputStream.WriteAsync(body, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
            response.Close();
        }
    }
}
