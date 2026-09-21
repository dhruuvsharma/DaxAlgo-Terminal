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

    /// <summary>
    /// Whether accepting the prompt may download and run the installer. True by default, so a
    /// configured feed gives the whole feature; false degrades to the original behaviour — the banner
    /// offers the release notes and the user installs by hand.
    ///
    /// <para>This switch only narrows: it can never turn anything on that <see cref="FeedUrl"/> and
    /// <see cref="FeedPublicKey"/> have not already enabled, and it cannot relax the signature and
    /// hash checks, which have no off switch at all.</para>
    /// </summary>
    public bool AllowAutomaticInstall { get; set; } = true;

    /// <summary>
    /// Optional Authenticode thumbprint the downloaded installer's signer must match exactly, as a
    /// hex string (spaces and case are ignored). Empty means "any signature Windows trusts", which is
    /// already narrow because the bytes must first match the <c>sha256</c> of the signed manifest —
    /// pinning simply removes the case where our manifest-signing key leaks but the code-signing
    /// certificate does not. Pin it in the shipped configuration.
    /// </summary>
    public string InstallerCertificateThumbprint { get; set; } = string.Empty;

    /// <summary>
    /// Hard ceiling on the installer download, in bytes. Enforced while streaming rather than from
    /// the declared content length, so a lying or endless response is cut off instead of filling the
    /// user's disk. Default 512 MiB; values below 1 fall back to the default.
    /// </summary>
    public long MaxInstallerBytes { get; set; } = 512L * 1024 * 1024;
}
