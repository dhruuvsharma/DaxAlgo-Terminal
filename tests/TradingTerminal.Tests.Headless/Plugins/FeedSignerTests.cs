using System.IO;
using System.Text;
using FluentAssertions;
using TradingTerminal.Infrastructure.Plugins.Feed;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

/// <summary>
/// The signer and verifier are a matched pair (issue #25): a key minted by <see cref="FeedSigner"/> signs
/// an index that the app's <see cref="FeedSignatureVerifier"/> — using only the paired public key — accepts,
/// and any tamper or key mismatch is rejected. This locks the publish-side crypto to the exact operation
/// the shipped app inverts.
/// </summary>
public sealed class FeedSignerTests
{
    private const string Index = """
        { "feedVersion": 1, "plugins": [ { "id": "a.plugin", "name": "A", "publisher": "P",
          "description": "d", "latest": { "version": "1.0.0", "sdkVersion": "0.1.0-alpha",
          "url": "https://x/a-1.0.0.daxplugin", "sha256": "AA" } } ] }
        """;

    [Fact]
    public void A_key_it_mints_signs_an_index_the_app_verifier_accepts()
    {
        var keys = FeedSigner.GenerateKeyPair();
        var indexBytes = Encoding.UTF8.GetBytes(Index);

        var signature = FeedSigner.Sign(indexBytes, keys.PrivateKeyBase64);
        var result = new FeedSignatureVerifier(keys.PublicKeyBase64).Verify(indexBytes, signature);

        result.Outcome.Should().Be(FeedVerifyOutcome.Ok);
        result.Index!.Plugins.Should().ContainSingle().Which.Id.Should().Be("a.plugin");
    }

    [Fact]
    public void A_tampered_index_no_longer_verifies()
    {
        var keys = FeedSigner.GenerateKeyPair();
        var signature = FeedSigner.Sign(Encoding.UTF8.GetBytes(Index), keys.PrivateKeyBase64);

        var tampered = Encoding.UTF8.GetBytes(Index.Replace("a.plugin", "evil.plugin"));
        var result = new FeedSignatureVerifier(keys.PublicKeyBase64).Verify(tampered, signature);

        result.Outcome.Should().Be(FeedVerifyOutcome.BadSignature);
    }

    [Fact]
    public void A_signature_from_a_different_key_is_rejected()
    {
        var signingKeys = FeedSigner.GenerateKeyPair();
        var otherKeys = FeedSigner.GenerateKeyPair();
        var indexBytes = Encoding.UTF8.GetBytes(Index);

        var signature = FeedSigner.Sign(indexBytes, signingKeys.PrivateKeyBase64);
        var result = new FeedSignatureVerifier(otherKeys.PublicKeyBase64).Verify(indexBytes, signature);

        result.Outcome.Should().Be(FeedVerifyOutcome.BadSignature);
    }

    [Fact]
    public void SignIndexFile_writes_a_detached_sig_beside_the_index_that_verifies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daxalgo-tests", "signer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var indexPath = Path.Combine(dir, "plugins-index.json");
            File.WriteAllText(indexPath, Index);
            var keys = FeedSigner.GenerateKeyPair();

            var sigPath = FeedSigner.SignIndexFile(indexPath, keys.PrivateKeyBase64);

            sigPath.Should().Be(indexPath + ".sig");
            var result = new FeedSignatureVerifier(keys.PublicKeyBase64)
                .Verify(File.ReadAllBytes(indexPath), File.ReadAllText(sigPath));
            result.Outcome.Should().Be(FeedVerifyOutcome.Ok);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
