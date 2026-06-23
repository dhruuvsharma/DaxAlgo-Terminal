namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Settings for the Upstox backend — an Indian-market broker (NSE/BSE equities, F&amp;O, commodities)
/// reached over the Upstox API v2/v3 (REST + WebSocket, no SDK).
///
/// <para>Auth is OAuth2 authorization-code. The login form opens
/// <c>https://api.upstox.com/v2/login/authorization/dialog?client_id={ApiKey}&amp;redirect_uri={RedirectUri}&amp;response_type=code</c>
/// in the browser; the user signs in, Upstox redirects to <see cref="RedirectUri"/> with a one-time
/// <c>code</c>, and the form exchanges that code for an <see cref="AccessToken"/> via
/// <c>POST /v2/login/authorization/token</c>. The token expires daily (~03:30 IST), so it is supplied
/// fresh at connect rather than persisted long-term.</para>
///
/// <para>Live data flows over the V3 protobuf market-data feed: <c>GET /v3/feed/market-data-feed/authorize</c>
/// returns an authorized <c>wss://</c> URL; the client connects, subscribes instrument keys in
/// <c>full</c> mode, and decodes the protobuf <c>FeedResponse</c> into L1 quotes + 5-level depth.
/// Historical candles and the instrument master come over REST. Data-only (this build places no orders);
/// the feed carries LTP + book but not a per-print trade tape. Docs: https://upstox.com/developer/api-documentation/.</para>
/// </summary>
public sealed class UpstoxOptions
{
    public const string SectionName = "Upstox";

    /// <summary>OAuth2 client id — the Upstox app's "API Key" from the developer console.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>OAuth2 client secret — the Upstox app's "API Secret". Used only for the code→token exchange.</summary>
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>Redirect URI registered on the Upstox app. Must match exactly (scheme/host/path/port).</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// OAuth2 access token. Normally filled by the login form's code-exchange step; can also be pasted
    /// directly. Expires daily (~03:30 IST) — a fresh authorization is required after expiry.
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Base REST URL. Override to pin a different API host/version without a rebuild; no trailing slash.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.upstox.com";

    public int ReconnectInitialDelaySeconds { get; set; } = 1;
    public int ReconnectMaxDelaySeconds { get; set; } = 30;
}
