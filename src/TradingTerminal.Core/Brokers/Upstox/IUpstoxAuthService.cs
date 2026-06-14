namespace TradingTerminal.Core.Brokers.Upstox;

/// <summary>
/// One-shot helper for the Upstox login form's OAuth2 authorization-code flow. The interface lives in
/// Core (broker-neutral seam) while the HTTP implementation lives in Infrastructure — mirroring the
/// <c>ICTraderAccountDiscovery</c> split — so the Login project can drive the flow without taking a
/// direct dependency on any networking concrete.
/// </summary>
public interface IUpstoxAuthService
{
    /// <summary>
    /// Builds the browser authorization URL the user opens to sign in and approve the app:
    /// <c>{baseUrl}/v2/login/authorization/dialog?response_type=code&amp;client_id={apiKey}&amp;redirect_uri={redirectUri}</c>.
    /// After approval Upstox redirects to <paramref name="redirectUri"/> with a one-time <c>code</c>
    /// query parameter.
    /// </summary>
    string BuildAuthorizationUrl(string baseUrl, string apiKey, string redirectUri);

    /// <summary>
    /// Exchanges a one-time authorization <paramref name="code"/> for an access token via
    /// <c>POST {baseUrl}/v2/login/authorization/token</c>. Returns the access token on success;
    /// throws with a user-facing message on failure (bad code, mismatched redirect URI, expired code).
    /// </summary>
    Task<string> ExchangeCodeForTokenAsync(
        string baseUrl,
        string apiKey,
        string apiSecret,
        string redirectUri,
        string code,
        CancellationToken ct = default);
}
