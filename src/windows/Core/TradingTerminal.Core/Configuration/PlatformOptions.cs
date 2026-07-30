namespace TradingTerminal.Core.Configuration;

/// <summary>Desktop connection settings for the DaxAlgo product platform.</summary>
public sealed class PlatformOptions
{
    public const string SectionName = "Platform";

    public const int DefaultTimeoutSeconds = 15;

    /// <summary>Absolute platform origin. HTTPS is required except for loopback development.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Base64-encoded SubjectPublicKeyInfo for the P-256 key trusted to sign edition leases.
    /// This is verification-only public material; no signing secret belongs in the desktop.
    /// </summary>
    public string EntitlementLeasePublicKey { get; set; } = string.Empty;

    /// <summary>Maximum duration of one platform request.</summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
}
