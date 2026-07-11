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
}
