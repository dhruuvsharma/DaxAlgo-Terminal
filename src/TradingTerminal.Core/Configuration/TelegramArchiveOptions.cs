namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Telegram (MTProto via WTelegramClient) credentials and session location. Bound from the
/// <c>TelegramArchive</c> section of appsettings; the api_hash and phone are persisted as
/// DPAPI-encrypted ciphertext (see <c>*EncryptedBase64</c> properties) by the App-layer
/// <c>ArchiveUserFile</c>, then decrypted into <see cref="ApiHash"/> / <see cref="PhoneNumber"/>
/// by a post-configure step at startup. Core itself stays platform-agnostic — these properties
/// are opaque strings here.
/// </summary>
public sealed class TelegramArchiveOptions
{
    public const string SectionName = "TelegramArchive";

    /// <summary>From https://my.telegram.org/apps. Required for any Telegram operation.</summary>
    public int ApiId { get; set; }

    /// <summary>Decrypted api_hash, populated either by reading the plaintext JSON field (legacy)
    /// or by the DPAPI post-configure step from <see cref="ApiHashEncryptedBase64"/>.</summary>
    public string ApiHash { get; set; } = string.Empty;

    /// <summary>Phone number in international format (+91…). Required for the first-time login.</summary>
    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>Where WTelegramClient persists its session (auth keys, DC mapping). Falls back to
    /// %LocalAppData%/DaxAlgoTerminal/telegram-session.bin when null.</summary>
    public string? SessionFilePath { get; set; }

    /// <summary>DPAPI-encrypted api_hash, base64-encoded. Persisted instead of the plaintext
    /// <see cref="ApiHash"/>; an App-layer post-configure step decrypts it back into ApiHash at
    /// startup. Null when the user hasn't logged in yet.</summary>
    public string? ApiHashEncryptedBase64 { get; set; }

    /// <summary>DPAPI-encrypted phone number, base64-encoded. Same persistence pattern as
    /// <see cref="ApiHashEncryptedBase64"/>.</summary>
    public string? PhoneNumberEncryptedBase64 { get; set; }
}
