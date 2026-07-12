using System;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using TradingTerminal.Infrastructure.Plugins.Feed;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

/// <summary>
/// The marketplace feed's trust anchor: a real ECDSA P-256 keypair signs a feed, and only that pinned
/// public key validates it — a tampered index, a wrong-key signature, an unsigned feed, or a
/// newer-than-supported schema are all rejected without a plugin ever being trusted.
/// </summary>
public sealed class FeedSignatureVerifierTests
{
    private const string SampleFeed = """
        { "feedVersion": 1, "publishedUtc": "2026-07-12T00:00:00Z",
          "plugins": [ { "id": "my.strategy", "name": "My Strategy", "publisher": "Someone",
            "description": "A demo.", "tags": ["momentum"],
            "latest": { "version": "1.0.0", "sdkVersion": "0.1.0-alpha",
              "url": "https://example/my.strategy-1.0.0.daxplugin", "sha256": "ABC", "sizeBytes": 1234 } } ],
          "revoked": [ { "id": "bad.plugin", "reason": "withdrawn" } ] }
        """;

    private static (string PublicKeyB64, byte[] Bytes, byte[] Signature) SignedFeed(string feed)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        var bytes = Encoding.UTF8.GetBytes(feed);
        var sig = ecdsa.SignData(bytes, HashAlgorithmName.SHA256);
        return (pub, bytes, sig);
    }

    [Fact]
    public void A_correctly_signed_feed_verifies_and_parses()
    {
        var (pub, bytes, sig) = SignedFeed(SampleFeed);

        var result = new FeedSignatureVerifier(pub).Verify(bytes, sig);

        result.Success.Should().BeTrue();
        result.Index!.Plugins.Should().ContainSingle().Which.Id.Should().Be("my.strategy");
        result.Index.Plugins[0].Latest.Sha256.Should().Be("ABC");
        result.Index.Revoked.Should().ContainSingle().Which.Id.Should().Be("bad.plugin");
    }

    [Fact]
    public void A_tampered_index_is_rejected()
    {
        var (pub, bytes, sig) = SignedFeed(SampleFeed);
        // Flip a byte in the "signed" content — the signature no longer matches.
        var tampered = (byte[])bytes.Clone();
        tampered[^5] ^= 0xFF;

        var result = new FeedSignatureVerifier(pub).Verify(tampered, sig);

        result.Success.Should().BeFalse();
        result.Outcome.Should().Be(FeedVerifyOutcome.BadSignature);
    }

    [Fact]
    public void A_signature_from_a_different_key_is_rejected()
    {
        var (_, bytes, _) = SignedFeed(SampleFeed);
        // Sign with a DIFFERENT key but verify against yet another pinned key.
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = attacker.SignData(bytes, HashAlgorithmName.SHA256);
        using var pinned = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pinnedPub = Convert.ToBase64String(pinned.ExportSubjectPublicKeyInfo());

        var result = new FeedSignatureVerifier(pinnedPub).Verify(bytes, forged);

        result.Outcome.Should().Be(FeedVerifyOutcome.BadSignature);
    }

    [Fact]
    public void No_pinned_key_means_the_feature_is_off_not_an_error()
    {
        var (_, bytes, sig) = SignedFeed(SampleFeed);
        var verifier = new FeedSignatureVerifier(string.Empty);

        verifier.IsConfigured.Should().BeFalse();
        verifier.Verify(bytes, sig).Outcome.Should().Be(FeedVerifyOutcome.NoPinnedKey);
    }

    [Fact]
    public void A_newer_schema_version_is_ignored_rather_than_misparsed()
    {
        var future = SampleFeed.Replace("\"feedVersion\": 1", "\"feedVersion\": 99");
        var (pub, bytes, sig) = SignedFeed(future);

        var result = new FeedSignatureVerifier(pub).Verify(bytes, sig);

        result.Outcome.Should().Be(FeedVerifyOutcome.UnsupportedVersion);
    }

    [Fact]
    public void Malformed_json_with_a_valid_signature_is_rejected_as_malformed()
    {
        var (pub, bytes, sig) = SignedFeed("{ not json ");

        var result = new FeedSignatureVerifier(pub).Verify(bytes, sig);

        result.Outcome.Should().Be(FeedVerifyOutcome.Malformed);
    }
}
