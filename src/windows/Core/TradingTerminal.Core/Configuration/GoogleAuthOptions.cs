namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Google OpenID Connect settings used by the pre-host product account gate.
/// </summary>
public sealed class GoogleAuthOptions
{
    public const string SectionName = "GoogleAuth";

    /// <summary>OAuth client id for the installed desktop application.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Optional OAuth client secret. Installed applications should normally rely on PKCE instead.
    /// </summary>
    public string? ClientSecret { get; set; }
}
