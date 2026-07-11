using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using TradingTerminal.Infrastructure.Plugins;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

/// <summary>
/// Exercises the real <see cref="AuthenticodeSignatureInspector"/> against genuinely-signed binaries —
/// the WinVerifyTrust <b>happy</b> path, which the fake-inspector tests in <c>PluginSecurityTests</c>
/// can't cover. The positive fixture is an embedded-Authenticode-signed Microsoft assembly that ships
/// with the .NET runtime this test is running on (<c>hostpolicy.dll</c> / <c>coreclr.dll</c> / …), so
/// no signing infrastructure or certificate install is needed.
/// </summary>
public sealed class AuthenticodeSignatureInspectorTests
{
    /// <summary>Embedded-signed (not catalog-signed) Microsoft binaries in the runtime folder.
    /// WinVerifyTrust with the file choice only sees EMBEDDED signatures, so catalog-signed OS files
    /// like kernel32.dll would read as unsigned — these do not.</summary>
    private static readonly string[] EmbeddedSignedCandidates =
        ["hostpolicy.dll", "hostfxr.dll", "coreclr.dll", "clrjit.dll"];

    private static string? FindSignedRuntimeBinary()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir is null) return null;

        return EmbeddedSignedCandidates
            .Select(name => Path.Combine(runtimeDir, name))
            .FirstOrDefault(File.Exists);
    }

    [Fact]
    public void Reports_a_genuinely_signed_Microsoft_binary_as_signed_and_valid()
    {
        if (!OperatingSystem.IsWindows())
            return; // WinVerifyTrust is Windows-only; the inspector returns Unsigned elsewhere by design.

        var signed = FindSignedRuntimeBinary();
        if (signed is null)
            return; // no embedded-signed runtime binary here — nothing to exercise the valid path with

        var signature = new AuthenticodeSignatureInspector().Inspect(signed);

        signature.IsSigned.Should().BeTrue("the .NET runtime binaries are Authenticode-signed by Microsoft");
        signature.IsValid.Should().BeTrue("WinVerifyTrust should validate a genuine Microsoft signature that chains to a trusted root");
        signature.Thumbprint.Should().NotBeNullOrWhiteSpace("a valid signature exposes its signer thumbprint for pinning");
        signature.Subject.Should().Contain("Microsoft");
    }

    [Fact]
    public void A_trusted_signed_binary_passes_a_Curated_policy_that_pins_its_thumbprint()
    {
        if (!OperatingSystem.IsWindows()) return;
        var signed = FindSignedRuntimeBinary();
        if (signed is null) return;

        var signature = new AuthenticodeSignatureInspector().Inspect(signed);
        if (!signature.IsValid) return;

        // The end-to-end trust decision: a Curated policy pinning the real signer thumbprint accepts it,
        // and one pinning a different thumbprint rejects it — proving the pin, not merely "any signature".
        PluginTrustPolicy.Curated([signature.Thumbprint])
            .Allows(signature, hasManifest: true, out _).Should().BeTrue();

        PluginTrustPolicy.Curated(["0000000000000000000000000000000000000000"])
            .Allows(signature, hasManifest: true, out var reason).Should().BeFalse();
        reason.Should().Contain("not a trusted publisher");
    }

    [Fact]
    public void Reports_an_unsigned_assembly_as_unsigned()
    {
        // This test assembly is not Authenticode-signed — the honest negative anchor for the above.
        var signature = new AuthenticodeSignatureInspector().Inspect(typeof(AuthenticodeSignatureInspectorTests).Assembly.Location);

        signature.IsSigned.Should().BeFalse();
    }
}
