using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using TradingTerminal.Infrastructure.Security;
using Xunit;

namespace TradingTerminal.Updates.Tests;

/// <summary>
/// The one signature primitive behind both the marketplace feed and the update feed. If these pass
/// for the wrong reason, every downstream "verified" claim is worthless — so each case pins a
/// specific way the check must reject, not just the happy path.
/// </summary>
public sealed class PinnedEcdsaVerifierTests
{
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"version":"1.4.0"}""");

    private static (PinnedEcdsaVerifier Verifier, byte[] Signature) SignWithFreshKey(byte[]? content = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var signature = key.SignData(content ?? Content, HashAlgorithmName.SHA256);
        return (new PinnedEcdsaVerifier(publicKey), signature);
    }

    [Fact]
    public void Accepts_a_signature_made_by_the_pinned_key()
    {
        var (verifier, signature) = SignWithFreshKey();

        verifier.Verify(Content, signature, out var detail).Should().Be(PinnedSignatureOutcome.Ok);
        detail.Should().BeNull();
    }

    [Fact]
    public void Rejects_content_altered_by_a_single_byte()
    {
        var (verifier, signature) = SignWithFreshKey();

        // "1.4.0" → "1.4.1": the smallest edit an attacker would actually want to make.
        var tampered = (byte[])Content.Clone();
        tampered[^3] = (byte)'1';

        verifier.Verify(tampered, signature, out _).Should().Be(PinnedSignatureOutcome.BadSignature);
    }

    [Fact]
    public void Rejects_a_signature_from_a_different_key()
    {
        var (verifier, _) = SignWithFreshKey();
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = attacker.SignData(Content, HashAlgorithmName.SHA256);

        verifier.Verify(Content, forged, out _).Should().Be(PinnedSignatureOutcome.BadSignature);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Reports_NoPinnedKey_when_nothing_is_pinned(string? pinned)
    {
        var verifier = new PinnedEcdsaVerifier(pinned);

        verifier.IsConfigured.Should().BeFalse();
        verifier.Verify(Content, new byte[64], out _).Should().Be(PinnedSignatureOutcome.NoPinnedKey);
    }

    [Fact]
    public void Rejects_rather_than_throws_on_a_malformed_signature_encoding()
    {
        var (verifier, _) = SignWithFreshKey();

        verifier.Verify(Content, "not base64 at all!!", out var detail)
            .Should().Be(PinnedSignatureOutcome.BadSignature);
        detail.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Rejects_rather_than_throws_on_a_malformed_pinned_key()
    {
        var verifier = new PinnedEcdsaVerifier("bm90LWEta2V5");  // valid base64, not a key

        verifier.IsConfigured.Should().BeTrue();
        verifier.Verify(Content, new byte[64], out _).Should().Be(PinnedSignatureOutcome.BadSignature);
    }

    [Fact]
    public void Accepts_the_base64_signature_overload_the_feed_actually_serves()
    {
        var (verifier, signature) = SignWithFreshKey();

        // The .sig file is base64 text and often ends with a newline the server or an editor added.
        var asServed = Convert.ToBase64String(signature) + "\n";

        verifier.Verify(Content, asServed, out _).Should().Be(PinnedSignatureOutcome.Ok);
    }
}
