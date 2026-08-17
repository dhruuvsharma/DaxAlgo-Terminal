using System;
using System.Security.Cryptography;

namespace TradingTerminal.Infrastructure.Security;

/// <summary>Why a detached signature was accepted or rejected.</summary>
public enum PinnedSignatureOutcome
{
    Ok,
    /// <summary>No public key is pinned — the dependent feature is OFF, which is not an error.</summary>
    NoPinnedKey,
    /// <summary>The signature does not verify against the pinned key, or is malformed.</summary>
    BadSignature,
}

/// <summary>
/// Verifies a DETACHED signature over exact content bytes against a pinned ECDSA P-256 public key
/// (SHA-256). The private key is held offline by the signer; only the pinned public key can validate,
/// so tampered or man-in-the-middled content is rejected before anything trusts it.
///
/// Verification is byte-exact over the raw content — never re-serialized — so whitespace or property
/// ordering cannot be used to slip a change past the signature.
///
/// This is the single implementation of that check. Both the plugin marketplace feed
/// (<c>Plugins.Feed.FeedSignatureVerifier</c>) and the application update feed
/// (<c>Updates.HttpUpdateChecker</c>) delegate here rather than each carrying their own copy —
/// two hand-rolled signature checks are how the two paths silently diverge.
/// </summary>
public sealed class PinnedEcdsaVerifier
{
    private readonly string _pinnedPublicKeyBase64;

    /// <param name="pinnedPublicKeyBase64">Base64 SubjectPublicKeyInfo of the ECDSA P-256 public key.
    /// Empty or whitespace means nothing is pinned, so the dependent feature stays off.</param>
    public PinnedEcdsaVerifier(string? pinnedPublicKeyBase64) =>
        _pinnedPublicKeyBase64 = pinnedPublicKeyBase64 ?? string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_pinnedPublicKeyBase64);

    /// <summary>
    /// Verifies <paramref name="content"/> against <paramref name="signature"/>. Never throws: a bad
    /// key, bad encoding or bad signature all classify, so callers degrade rather than crash.
    /// </summary>
    public PinnedSignatureOutcome Verify(byte[] content, byte[] signature, out string? detail)
    {
        detail = null;
        if (!IsConfigured)
        {
            detail = "No public key is pinned.";
            return PinnedSignatureOutcome.NoPinnedKey;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(_pinnedPublicKeyBase64), out _);
            if (!ecdsa.VerifyData(content, signature, HashAlgorithmName.SHA256))
            {
                detail = "The signature does not verify against the pinned key.";
                return PinnedSignatureOutcome.BadSignature;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            detail = $"Signature check failed: {ex.Message}";
            return PinnedSignatureOutcome.BadSignature;
        }

        return PinnedSignatureOutcome.Ok;
    }

    /// <summary>Overload taking the signature as base64 text, as it is stored beside the content.</summary>
    public PinnedSignatureOutcome Verify(byte[] content, string signatureBase64, out string? detail)
    {
        try
        {
            return Verify(content, Convert.FromBase64String((signatureBase64 ?? string.Empty).Trim()), out detail);
        }
        catch (FormatException ex)
        {
            detail = $"Bad signature encoding: {ex.Message}";
            return PinnedSignatureOutcome.BadSignature;
        }
    }
}
