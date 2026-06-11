namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Settings for the Ironbeam backend — a futures FCM exposed over a REST + WebSocket API v2 (no
/// SDK). Auth is a JWT minted by <c>POST /v2/auth</c> from a username + API key (the API key is
/// sent as the request's <c>password</c> field; non-Enterprise accounts authenticate this way).
///
/// Hosts: <c>https://demo.ironbeamapi.com/v2</c> (paper) or <c>https://live.ironbeamapi.com/v2</c>
/// (funded), selected by <see cref="IsLive"/>; set <see cref="BaseUrlOverride"/> to pin a different
/// host / API version without a rebuild. Market data flows through a server-created stream: call
/// <c>GET /stream/create</c> to get a stream id, then connect
/// <c>wss://{host}/v2/stream/{streamId}?token=...</c>. Each stream is capped at <b>10 symbols</b>.
/// The feed carries L1 quotes, L2 depth, and a real trade tape. Docs: https://docs.ironbeamapi.com/.
/// </summary>
public sealed class IronBeamOptions
{
    public const string SectionName = "IronBeam";

    /// <summary>Ironbeam account username.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// API key — sent as the <c>password</c> field of <c>POST /v2/auth</c>. Non-Enterprise accounts
    /// authenticate with username + API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// True targets https://live.ironbeamapi.com/v2 (funded); false targets
    /// https://demo.ironbeamapi.com/v2 (paper).
    /// </summary>
    public bool IsLive { get; set; }

    /// <summary>
    /// When non-empty, overrides the demo/live base URL entirely — lets users pin a different API
    /// version / host without a rebuild. No trailing slash.
    /// </summary>
    public string BaseUrlOverride { get; set; } = string.Empty;

    public int ReconnectInitialDelaySeconds { get; set; } = 1;
    public int ReconnectMaxDelaySeconds { get; set; } = 30;
}
