namespace TradingTerminal.Core.Configuration;

/// <summary>How much the host trusts the plugins it finds in its plugins folder.</summary>
public enum PluginTrustMode
{
    /// <summary>Load anything, unsigned included, without inspecting signatures. The dev / open-core
    /// default: you build the plugins yourself.</summary>
    Permissive,

    /// <summary>Curated marketplace: require a <c>plugin.json</c> and a valid Authenticode signature
    /// whose signer thumbprint is pinned in <see cref="PluginsOptions.TrustedThumbprints"/>. An
    /// in-process strategy gets the user's broker session and cannot be sandboxed, so publisher
    /// pinning — not merely "some valid signature" — is the control.</summary>
    Curated,
}

/// <summary>What the host does with the static IL policy scan's verdict.</summary>
public enum PluginScanMode
{
    /// <summary>Refuse to load a plugin whose IL contains Block-level capabilities (P/Invoke, starting
    /// processes, registry, Reflection.Emit, loading assemblies) and quarantine it. Warn-level
    /// capabilities (file / network I/O) load but are surfaced. The default.</summary>
    Enforce,

    /// <summary>Scan and report, but load anyway. For debugging a plugin the scanner blocks.</summary>
    WarnOnly,

    /// <summary>Don't scan at all.</summary>
    Off,
}

/// <summary>
/// Binds the <c>Plugins</c> configuration section. Replaces the trust policy that each shell used to
/// hardcode to <see cref="PluginTrustMode.Permissive"/>, so a distribution build can pin its
/// publishers without a code change.
/// </summary>
public sealed class PluginsOptions
{
    public const string SectionName = "Plugins";

    /// <summary>Defaults to <see cref="PluginTrustMode.Permissive"/> — the dev/open-core flow, and the
    /// only mode that works until first-party plugins are code-signed (Curated would reject every
    /// unsigned plugin and leave an empty strategy catalog).</summary>
    public PluginTrustMode TrustPolicy { get; set; } = PluginTrustMode.Permissive;

    /// <summary>Publisher certificate thumbprints trusted in <see cref="PluginTrustMode.Curated"/>
    /// mode. Compared case-insensitively with spaces stripped.</summary>
    public IList<string> TrustedThumbprints { get; set; } = [];

    /// <summary>What to do with the IL policy scan's verdict. Enforced by default — unlike trust, the
    /// scan is safe to enforce in every edition today (all nine first-party plugins scan clean of
    /// Block-level capabilities).</summary>
    public PluginScanMode ScanMode { get; set; } = PluginScanMode.Enforce;

    /// <summary>URL of the signed marketplace index (<c>plugins-index.json</c>). Empty ⇒ no feed (the
    /// catalog tab is empty). Fetched in the background, never on the startup path.</summary>
    public string FeedUrl { get; set; } = string.Empty;

    /// <summary>Base64-encoded SubjectPublicKeyInfo of the ECDSA P-256 public key the feed's detached
    /// signature must verify against — pinned in the app. An index whose signature doesn't verify against
    /// this key is ignored (Activity Log warning), so a tampered or unsigned feed can't inject plugins.</summary>
    public string FeedPublicKey { get; set; } = string.Empty;
}
