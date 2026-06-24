using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers.Upstox;

namespace TradingTerminal.Infrastructure.Upstox;

/// <summary>
/// HTTP implementation of the Upstox OAuth2 authorization-code flow (interface in Core so the Login
/// project stays networking-agnostic). Builds the browser sign-in URL and exchanges the returned
/// one-time <c>code</c> for an access token via <c>POST /v2/login/authorization/token</c>.
/// </summary>
internal sealed class UpstoxAuthService : IUpstoxAuthService
{
    private readonly HttpClient _http = new();
    private readonly ILogger<UpstoxAuthService> _logger;

    public UpstoxAuthService(ILogger<UpstoxAuthService> logger) => _logger = logger;

    public string BuildAuthorizationUrl(string baseUrl, string apiKey, string redirectUri)
    {
        var root = baseUrl.TrimEnd('/');
        return $"{root}/v2/login/authorization/dialog?response_type=code" +
               $"&client_id={Uri.EscapeDataString(apiKey)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}";
    }

    public async Task<string> ExchangeCodeForTokenAsync(
        string baseUrl, string apiKey, string apiSecret, string redirectUri, string code,
        CancellationToken ct = default)
    {
        var root = baseUrl.TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{root}/v2/login/authorization/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code.Trim(),
                ["client_id"] = apiKey.Trim(),
                ["client_secret"] = apiSecret.Trim(),
                ["redirect_uri"] = redirectUri.Trim(),
                ["grant_type"] = "authorization_code",
            }),
        };
        req.Headers.Accept.ParseAdd("application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var detail = ExtractError(bytes);
            _logger.LogWarning("Upstox token exchange failed ({Status}): {Detail}", (int)resp.StatusCode, detail);
            throw new InvalidOperationException(
                $"Upstox token exchange failed ({(int)resp.StatusCode}): {detail}. " +
                "Check the API key/secret, that the redirect URI matches the app exactly, and that the code is fresh (codes are single-use).");
        }

        using var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.TryGetProperty("access_token", out var tokenEl)
            && tokenEl.ValueKind == JsonValueKind.String
            && tokenEl.GetString() is { Length: > 0 } token)
        {
            return token;
        }
        throw new InvalidOperationException("Upstox token response did not contain an access_token.");
    }

    private static string ExtractError(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // Upstox error shape: { "errors": [ { "message": "...", "errorCode": "..." } ] }
            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array
                && errs.GetArrayLength() > 0 && errs[0].TryGetProperty("message", out var m))
                return m.GetString() ?? "unknown error";
            if (root.TryGetProperty("error_description", out var d))
                return d.GetString() ?? "unknown error";
        }
        catch { /* fall through to raw */ }
        var raw = System.Text.Encoding.UTF8.GetString(body);
        return raw.Length <= 256 ? raw : raw[..256];
    }
}
