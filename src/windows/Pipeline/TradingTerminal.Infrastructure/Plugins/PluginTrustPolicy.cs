using System.Collections.Generic;
using System.Linq;

namespace TradingTerminal.Infrastructure.Plugins;

/// <summary>
/// Decides whether a plugin is allowed to load. This is the curated-marketplace gate: production
/// pins the publisher certificate thumbprints it trusts and requires a valid signature, while the
/// open-core dev build is <see cref="Permissive"/> (loads unsigned local plugins). Thumbprint
/// PINNING — not merely "any valid signature" — is the curation control: only DLLs signed by a known
/// publisher load, because an in-process strategy gets the user's broker session and can't be
/// sandboxed.
/// </summary>
public sealed record PluginTrustPolicy(
    bool RequireSignature,
    bool RequireManifest,
    IReadOnlySet<string> TrustedPublisherThumbprints)
{
    /// <summary>Dev / open-core default: load anything (the existing app + CLI flow). No signature or
    /// manifest required; signatures aren't even inspected.</summary>
    public static PluginTrustPolicy Permissive { get; } =
        new(RequireSignature: false, RequireManifest: false, new HashSet<string>());

    /// <summary>Curated marketplace policy: require a manifest and a valid signature whose signer
    /// thumbprint is one of <paramref name="trustedThumbprints"/>.</summary>
    public static PluginTrustPolicy Curated(IEnumerable<string> trustedThumbprints) =>
        new(RequireSignature: true,
            RequireManifest: true,
            trustedThumbprints.Select(Normalize).Where(t => t.Length > 0).ToHashSet());

    /// <summary>True when a plugin with the given signature and manifest presence may load. On
    /// rejection, <paramref name="reason"/> explains why (for logging).</summary>
    public bool Allows(PluginSignature signature, bool hasManifest, out string? reason)
    {
        if (RequireManifest && !hasManifest)
        {
            reason = "a plugin manifest (plugin.json) is required but is missing";
            return false;
        }

        if (!RequireSignature)
        {
            reason = null;
            return true;
        }

        if (!signature.IsSigned || !signature.IsValid)
        {
            reason = "a valid code signature is required, but the assembly is unsigned or its signature is invalid";
            return false;
        }

        var thumb = Normalize(signature.Thumbprint);
        if (thumb.Length == 0 || !TrustedPublisherThumbprints.Contains(thumb))
        {
            reason = $"signer thumbprint '{signature.Thumbprint}' is not a trusted publisher";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>Thumbprints are compared case-insensitively with spaces stripped (Windows cert UIs add
    /// spaces; some sources lower-case).</summary>
    private static string Normalize(string? thumbprint) =>
        thumbprint?.Replace(" ", string.Empty).ToUpperInvariant() ?? string.Empty;
}
