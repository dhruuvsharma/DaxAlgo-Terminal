namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Application update checking, bound from the <c>Updates</c> configuration section.
///
/// **Off unless both <see cref="FeedUrl"/> and <see cref="FeedPublicKey"/> are set.** An update feed
/// without a pinned key would let anyone who can answer for that host tell the app a new version
/// exists and where to get it, so an unsigned feed is not a degraded mode — it is simply not a
/// feature. This mirrors how <see cref="PluginsOptions.FeedUrl"/> / <see cref="PluginsOptions.FeedPublicKey"/>
/// gate the marketplace feed.
/// </summary>
public sealed class UpdatesOptions
{
    public const string SectionName = "Updates";

    /// <summary>
    /// Absolute URL of the signed release manifest (JSON). The detached signature is fetched from the
    /// same URL with <c>.sig</c> appended. Empty ⇒ update checking is off.
    /// </summary>
    public string FeedUrl { get; set; } = string.Empty;

    /// <summary>
    /// Base64 SubjectPublicKeyInfo of the ECDSA P-256 public key the manifest is signed with. Empty ⇒
    /// update checking is off. Pin the key in the shipped configuration; never fetch it from the feed.
    /// </summary>
    public string FeedPublicKey { get; set; } = string.Empty;

    /// <summary>Check once shortly after start-up. Default true.</summary>
    public bool CheckOnStartup { get; set; } = true;

    /// <summary>
    /// Hours between background re-checks. Values below 1 are clamped to 1 so a misconfiguration
    /// cannot turn the app into a polling loop against the release host.
    /// </summary>
    public int CheckIntervalHours { get; set; } = 24;
}
