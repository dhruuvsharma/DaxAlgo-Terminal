using System.IO;
using System.Security.Cryptography;

namespace TradingTerminal.Infrastructure.Plugins.Feed;

/// <summary>
/// The offline / CI counterpart to <see cref="FeedSignatureVerifier"/>: mints the ECDSA P-256 keypair
/// whose public half is pinned in the app (<c>PluginsOptions.FeedPublicKey</c>), and produces the DETACHED
/// signature over the raw <c>plugins-index.json</c> bytes that the verifier checks. It is byte-for-byte the
/// same operation the verifier inverts (SHA-256, a raw signature over the raw index bytes with no
/// re-serialization), so what this signs is exactly what the app will accept.
/// <para>
/// This is <b>publish-side maintainer / CI tooling</b> — it takes a PRIVATE key that must never reach the
/// app or a user machine (keep it offline, or as a CI secret). The shipped app only ever holds the public
/// key and only ever verifies. See <c>docs/marketplace-hosting.md</c> for the key-custody + publish flow.
/// </para>
/// </summary>
public static class FeedSigner
{
    /// <summary>A fresh feed-signing keypair.</summary>
    /// <param name="PrivateKeyBase64">Base64 PKCS#8 private key — kept OFFLINE / secret; used only to sign.</param>
    /// <param name="PublicKeyBase64">Base64 SubjectPublicKeyInfo public key — paste into
    /// <c>PluginsOptions.FeedPublicKey</c> so the app can verify what this key signs.</param>
    public sealed record FeedKeyPair(string PrivateKeyBase64, string PublicKeyBase64);

    /// <summary>Generates a new ECDSA P-256 keypair for signing the feed.</summary>
    public static FeedKeyPair GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new FeedKeyPair(
            Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()),
            Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>Signs <paramref name="indexBytes"/> with the base64 PKCS#8 private key and returns the
    /// detached signature as base64 — the exact contents to store beside the index as
    /// <c>&lt;index&gt;.sig</c>.</summary>
    public static string Sign(byte[] indexBytes, string privateKeyBase64)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);
        return Convert.ToBase64String(ecdsa.SignData(indexBytes, HashAlgorithmName.SHA256));
    }

    /// <summary>Reads the index at <paramref name="indexPath"/>, signs its raw bytes, and writes the
    /// detached signature to <c>&lt;indexPath&gt;.sig</c> (what the feed serves next to the index).
    /// Returns the signature file path.</summary>
    public static string SignIndexFile(string indexPath, string privateKeyBase64)
    {
        var signature = Sign(File.ReadAllBytes(indexPath), privateKeyBase64);
        var signaturePath = indexPath + ".sig";
        File.WriteAllText(signaturePath, signature);
        return signaturePath;
    }
}
