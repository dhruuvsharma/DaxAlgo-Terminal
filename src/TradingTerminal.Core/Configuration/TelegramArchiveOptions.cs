namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Telegram (MTProto via WTelegramClient) credentials and session location. Bound from the
/// <c>TelegramArchive</c> section of appsettings; the API id/hash are not secrets per se (every
/// Telegram client publishes them) but the session file IS — it grants account-level access and
/// lives outside the repo by default.
/// </summary>
public sealed class TelegramArchiveOptions
{
    public const string SectionName = "TelegramArchive";

    /// <summary>From https://my.telegram.org/apps. Required for any Telegram operation.</summary>
    public int ApiId { get; set; }

    public string ApiHash { get; set; } = string.Empty;

    /// <summary>Phone number in international format (+91…). Required for the first-time login.</summary>
    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>Where WTelegramClient persists its session (auth keys, DC mapping). Falls back to
    /// %LocalAppData%/DaxAlgoTerminal/telegram-session.bin when null.</summary>
    public string? SessionFilePath { get; set; }
}
