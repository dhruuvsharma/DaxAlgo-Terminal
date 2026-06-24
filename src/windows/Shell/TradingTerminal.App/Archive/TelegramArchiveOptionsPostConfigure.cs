using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Archive;

/// <summary>
/// Runs after <see cref="TelegramArchiveOptions"/> is bound from configuration and replaces the
/// encrypted <c>*EncryptedBase64</c> fields with their DPAPI-decrypted plaintext on the
/// <see cref="TelegramArchiveOptions.ApiHash"/> / <see cref="TelegramArchiveOptions.PhoneNumber"/>
/// runtime properties. Plaintext values written by older builds are left intact — they get
/// upgraded to the encrypted form on next save.
/// </summary>
internal sealed class TelegramArchiveOptionsPostConfigure : IPostConfigureOptions<TelegramArchiveOptions>
{
    public void PostConfigure(string? name, TelegramArchiveOptions options)
    {
        if (!string.IsNullOrEmpty(options.ApiHashEncryptedBase64))
        {
            var decrypted = TelegramArchiveCredentialProtection.Decrypt(options.ApiHashEncryptedBase64);
            if (!string.IsNullOrEmpty(decrypted)) options.ApiHash = decrypted;
        }

        if (!string.IsNullOrEmpty(options.PhoneNumberEncryptedBase64))
        {
            var decrypted = TelegramArchiveCredentialProtection.Decrypt(options.PhoneNumberEncryptedBase64);
            if (!string.IsNullOrEmpty(decrypted)) options.PhoneNumber = decrypted;
        }
    }
}
