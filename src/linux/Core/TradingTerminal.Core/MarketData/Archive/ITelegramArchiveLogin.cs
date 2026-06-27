namespace TradingTerminal.Core.MarketData.Archive;

/// <summary>
/// Telegram credentials for the market-data archive offloader, as plain opaque strings. Lets the
/// login window collect + submit them without referencing Infrastructure or the WTelegram SDK.
/// </summary>
/// <param name="ApiId">api_id from https://my.telegram.org/apps.</param>
/// <param name="ApiHash">api_hash from https://my.telegram.org/apps.</param>
/// <param name="PhoneNumber">Phone number in international format (e.g. +91XXXXXXXXXX).</param>
public sealed record TelegramArchiveCredentials(int ApiId, string ApiHash, string PhoneNumber);

/// <summary>Outcome of a <see cref="ITelegramArchiveLogin.ConnectAsync"/> attempt.</summary>
/// <param name="Success">True when the transport reported a ready, signed-in session.</param>
/// <param name="Message">Human-readable status for the UI (success label or failure reason).</param>
public sealed record TelegramArchiveLoginResult(bool Success, string Message);

/// <summary>
/// Login seam for the Telegram archive-offload credentials, surfaced on the login window. The
/// concrete implementation lives in the App layer (it owns persistence + the WTelegram transport +
/// the interactive verification-code/2FA prompt); the login window depends only on this Core
/// interface so it stays free of Infrastructure references. This is auth + persistence only — the
/// actual archive upload path is unchanged and lives in the offloader.
/// </summary>
public interface ITelegramArchiveLogin
{
    /// <summary>True when a signed-in Telegram session is already available.</summary>
    bool IsConnected { get; }

    /// <summary>Current persisted credentials (api_id / api_hash / phone), for pre-filling the form.</summary>
    TelegramArchiveCredentials Load();

    /// <summary>Persists the supplied credentials, then drives the Telegram sign-in (which may pop the
    /// verification-code / 2FA prompt). Returns whether the session ended up connected.</summary>
    Task<TelegramArchiveLoginResult> ConnectAsync(
        TelegramArchiveCredentials credentials, CancellationToken cancellationToken = default);
}
