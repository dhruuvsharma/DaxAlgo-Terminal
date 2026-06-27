#if WINDOWS
using System.Security.Cryptography;
#endif
using System.Text;

namespace TradingTerminal.App.Archive;

/// <summary>
/// Protection helpers for Telegram archive credentials. On Windows the secret is encrypted with DPAPI
/// (<c>DataProtectionScope.CurrentUser</c>) — decryptable only by the same Windows user on the same
/// machine. Off Windows (the Avalonia/Linux head) DPAPI is unavailable, so we fall back to a plain
/// base64 wrapper — the same precedent Phase 0 set for AiAnalystOptions (TODO: an OS-keyring-backed
/// secret store on Linux). The fallback never throws.
/// </summary>
public static class TelegramArchiveCredentialProtection
{
    public static string? Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;
#if WINDOWS
        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
#else
        // Non-Windows: no DPAPI. Base64 the UTF-8 bytes (obfuscation only — not encryption).
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
#endif
    }

    public static string? Decrypt(string? cipherBase64)
    {
        if (string.IsNullOrEmpty(cipherBase64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(cipherBase64);
#if WINDOWS
            var plain = ProtectedData.Unprotect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
#else
            return Encoding.UTF8.GetString(bytes);
#endif
        }
#if WINDOWS
        catch (CryptographicException) { return null; }
#endif
        catch (FormatException) { return null; }
    }
}
