using System.Text;
using TradingTerminal.Infrastructure.Security;
using System.Text.Json;

namespace TradingTerminal.Infrastructure.Plugins.Feed;

/// <summary>Why a feed was accepted or rejected.</summary>
public enum FeedVerifyOutcome
{
    Ok,
    NoPinnedKey,        // the app has no pinned feed key — feature off, not an error
    BadSignature,       // the detached signature doesn't verify against the pinned key
    Malformed,          // the index JSON couldn't be parsed
    UnsupportedVersion, // feedVersion is newer than this app understands
}

/// <summary>The result of verifying + parsing a feed.</summary>
public sealed record FeedVerifyResult(FeedVerifyOutcome Outcome, PluginIndex? Index, string? Detail)
{
    public bool Success => Outcome == FeedVerifyOutcome.Ok && Index is not null;
}

/// <summary>
/// Verifies the marketplace index against a pinned ECDSA P-256 public key and parses it. The feed ships
/// a DETACHED signature over the exact index bytes; the signer holds the private key offline, and only
/// this pinned public key can validate it — so a tampered index (or an unsigned one served by a
/// man-in-the-middle) is rejected before a single plugin is trusted. Verification is byte-exact over the
/// raw index content (no re-serialization), so whitespace/ordering can't be used to slip a change past
/// the signature.
/// </summary>
public sealed class FeedSignatureVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly PinnedEcdsaVerifier _signature;

    /// <param name="pinnedPublicKeyBase64">Base64 SubjectPublicKeyInfo of the ECDSA P-256 public key
    /// (from <c>PluginsOptions.FeedPublicKey</c>). Empty ⇒ the feature is off.</param>
    public FeedSignatureVerifier(string pinnedPublicKeyBase64) =>
        _signature = new PinnedEcdsaVerifier(pinnedPublicKeyBase64);

    public bool IsConfigured => _signature.IsConfigured;

    /// <summary>Verifies <paramref name="indexBytes"/> against <paramref name="signatureBytes"/> (a raw
    /// ECDSA signature, base64-decoded by the caller) with the pinned key, then parses the index. Any
    /// failure returns a classified outcome — this never throws, so a bad feed degrades to "no feed".</summary>
    public FeedVerifyResult Verify(byte[] indexBytes, byte[] signatureBytes)
    {
        // The byte-exact ECDSA check lives in one place (Security.PinnedEcdsaVerifier) and is shared
        // with the application update feed; only the outcome mapping and parsing are feed-specific.
        switch (_signature.Verify(indexBytes, signatureBytes, out var detail))
        {
            case PinnedSignatureOutcome.NoPinnedKey:
                return new FeedVerifyResult(FeedVerifyOutcome.NoPinnedKey, null, "No feed public key is pinned.");
            case PinnedSignatureOutcome.BadSignature:
                return new FeedVerifyResult(FeedVerifyOutcome.BadSignature, null, detail);
        }

        PluginIndex? index;
        try
        {
            index = JsonSerializer.Deserialize<PluginIndex>(indexBytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new FeedVerifyResult(FeedVerifyOutcome.Malformed, null, $"Malformed feed index: {ex.Message}");
        }

        if (index is null || index.Plugins is null)
            return new FeedVerifyResult(FeedVerifyOutcome.Malformed, null, "Feed index is empty or missing 'plugins'.");
        if (index.FeedVersion > PluginIndex.SupportedFeedVersion)
            return new FeedVerifyResult(FeedVerifyOutcome.UnsupportedVersion, null,
                $"Feed schema v{index.FeedVersion} is newer than this app supports (v{PluginIndex.SupportedFeedVersion}).");

        return new FeedVerifyResult(FeedVerifyOutcome.Ok, index, null);
    }

    /// <summary>Convenience overload taking the signature as base64 text (as it's stored beside the feed).</summary>
    public FeedVerifyResult Verify(byte[] indexBytes, string signatureBase64)
    {
        try { return Verify(indexBytes, Convert.FromBase64String(signatureBase64.Trim())); }
        catch (FormatException ex) { return new FeedVerifyResult(FeedVerifyOutcome.BadSignature, null, $"Bad signature encoding: {ex.Message}"); }
    }

    internal static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
