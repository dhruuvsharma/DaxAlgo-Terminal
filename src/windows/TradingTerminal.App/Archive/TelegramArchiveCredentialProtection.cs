using System.Security.Cryptography;
using System.Text;

namespace TradingTerminal.App.Archive;

/// <summary>
/// DPAPI helpers for Telegram archive credentials. Mirrors the pattern used by
/// <c>StoredCredentials</c> for broker secrets — base64-encoded ciphertext under
/// <see cref="DataProtectionScope.CurrentUser"/>, so it's decryptable only by the same Windows
/// user on the same machine. Treat as a thin convenience wrapper; the actual keys belong to DPAPI.
/// </summary>
internal static class TelegramArchiveCredentialProtection
{
    public static string? Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;
        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string? Decrypt(string? cipherBase64)
    {
        if (string.IsNullOrEmpty(cipherBase64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(cipherBase64);
            var plain = ProtectedData.Unprotect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }
}
