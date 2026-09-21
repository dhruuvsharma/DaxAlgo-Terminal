using System;
using System.IO;
using System.Security.Cryptography;
using TradingTerminal.Core.Updates;
using TradingTerminal.Infrastructure.Plugins;

namespace TradingTerminal.Infrastructure.Updates;

/// <summary>Why a staged installer was rejected. Null is the only "it is fine" answer.</summary>
internal sealed record InstallerTrustFailure(UpdateDownloadOutcome Outcome, string Detail);

/// <summary>
/// The single answer to "is this file safe to execute?" — hash against the signed manifest, then
/// Authenticode, then an optional signer pin.
///
/// <para>It exists as one class for the same reason <c>Security.PinnedEcdsaVerifier</c> does: the
/// downloader checks a file it has just written and the installer re-checks it a moment before
/// launching, and two hand-rolled copies of that rule are how the two paths silently drift until one
/// of them is weaker than the other. Both call here.</para>
///
/// <para>Every failure path rejects. A file that cannot be read, hashed or inspected is untrusted,
/// never trusted-by-default.</para>
/// </summary>
internal sealed class InstallerTrust
{
    private readonly IPluginSignatureInspector _signatures;
    private readonly string _pinnedThumbprint;

    /// <param name="signatures">The Authenticode inspector to ask.</param>
    /// <param name="pinnedThumbprint">Exact signer thumbprint to require; empty accepts any
    /// Authenticode signature Windows trusts, which is already narrowed by the hash check.</param>
    internal InstallerTrust(IPluginSignatureInspector signatures, string? pinnedThumbprint)
    {
        _signatures = signatures;
        _pinnedThumbprint = Normalize(pinnedThumbprint);
    }

    /// <summary>Returns null when the file may be executed.</summary>
    /// <param name="path">The staged installer to judge.</param>
    /// <param name="expectedSha256">Lower-case hex hash from the SIGNED manifest, or null to check
    /// the signature alone (the installer's pre-launch re-check, where the hash was already proved).</param>
    internal InstallerTrustFailure? Verify(string path, string? expectedSha256 = null)
    {
        if (!File.Exists(path))
            return new InstallerTrustFailure(UpdateDownloadOutcome.Failed, "The staged installer is gone.");

        if (expectedSha256 is not null)
        {
            string actual;
            try
            {
                using var stream = File.OpenRead(path);
                actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new InstallerTrustFailure(UpdateDownloadOutcome.Failed,
                    $"The staged installer could not be read: {ex.Message}");
            }

            if (!string.Equals(actual, Normalize(expectedSha256), StringComparison.Ordinal))
                return new InstallerTrustFailure(UpdateDownloadOutcome.HashMismatch,
                    "The staged installer does not match the hash in the signed manifest.");
        }

        var signature = _signatures.Inspect(path);
        if (!signature.IsSigned || !signature.IsValid)
            return new InstallerTrustFailure(UpdateDownloadOutcome.UntrustedInstaller,
                "The installer is unsigned, or its Authenticode signature does not verify.");

        if (_pinnedThumbprint.Length > 0 &&
            !string.Equals(Normalize(signature.Thumbprint), _pinnedThumbprint, StringComparison.Ordinal))
            return new InstallerTrustFailure(UpdateDownloadOutcome.UntrustedInstaller,
                $"The installer is signed by '{signature.Subject}', which is not the pinned certificate.");

        return null;
    }

    /// <summary>Thumbprints are compared case- and space-insensitively; certutil, the Windows UI and
    /// signtool each format them differently.</summary>
    internal static string Normalize(string? value) =>
        (value ?? string.Empty).Replace(" ", string.Empty).Trim().ToLowerInvariant();

    /// <summary>True for exactly 64 hex characters — the only shape a SHA-256 can take.</summary>
    internal static bool IsSha256Hex(string value)
    {
        if (value.Length != 64) return false;
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }
}
