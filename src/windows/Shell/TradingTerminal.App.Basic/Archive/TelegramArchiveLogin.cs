using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.MarketData.Archive;
using TradingTerminal.Infrastructure.MarketData.Archive.Telegram;

namespace TradingTerminal.App.Archive;

/// <summary>
/// App-layer implementation of the <see cref="ITelegramArchiveLogin"/> seam used by the login
/// window. Reuses the exact persistence (<see cref="ArchiveUserFile"/> → DPAPI-encrypted
/// <c>archive.json</c>) and connect path (<see cref="TelegramArchiveTransport.EnsureConnectedAsync"/>,
/// which pops the verification-code / 2FA dialog via the registered WPF prompt) as the in-app
/// Archive Settings tab, so both surfaces share one source of truth and never diverge.
/// </summary>
public sealed class TelegramArchiveLogin : ITelegramArchiveLogin
{
    private readonly IOptionsMonitor<ArchiveOptions> _archiveOpts;
    private readonly IOptionsMonitor<TelegramArchiveOptions> _telegramOpts;
    private readonly TelegramArchiveTransport _transport;
    private readonly ILogger<TelegramArchiveLogin> _logger;

    public TelegramArchiveLogin(
        IOptionsMonitor<ArchiveOptions> archiveOpts,
        IOptionsMonitor<TelegramArchiveOptions> telegramOpts,
        TelegramArchiveTransport transport,
        ILogger<TelegramArchiveLogin> logger)
    {
        _archiveOpts = archiveOpts;
        _telegramOpts = telegramOpts;
        _transport = transport;
        _logger = logger;
    }

    public bool IsConnected => _transport.IsReady;

    public TelegramArchiveCredentials Load()
    {
        var t = _telegramOpts.CurrentValue;
        return new TelegramArchiveCredentials(t.ApiId, t.ApiHash, t.PhoneNumber);
    }

    public async Task<TelegramArchiveLoginResult> ConnectAsync(
        TelegramArchiveCredentials credentials, CancellationToken cancellationToken = default)
    {
        // Pre-flight: surface missing fields with a clear message instead of letting WTelegram throw
        // "value cannot be an empty string" deep inside the auth flow.
        if (credentials.ApiId <= 0)
            return new TelegramArchiveLoginResult(false, "Enter your Telegram api_id (a number from my.telegram.org/apps).");
        if (string.IsNullOrWhiteSpace(credentials.ApiHash))
            return new TelegramArchiveLoginResult(false, "Enter your Telegram api_hash (from my.telegram.org/apps).");
        if (string.IsNullOrWhiteSpace(credentials.PhoneNumber))
            return new TelegramArchiveLoginResult(false, "Enter your phone number in international format (e.g. +91XXXXXXXXXX).");

        var snap = new TelegramArchiveOptions
        {
            ApiId = credentials.ApiId,
            ApiHash = credentials.ApiHash.Trim(),
            PhoneNumber = credentials.PhoneNumber.Trim(),
            SessionFilePath = _telegramOpts.CurrentValue.SessionFilePath,
        };

        // Persist first (preserving the existing archive settings) so the next launch reads the creds
        // from disk; ArchiveUserFile DPAPI-encrypts api_hash + phone.
        ArchiveUserFile.Save(_archiveOpts.CurrentValue, snap);

        try
        {
            // Pass the in-memory snapshot straight to the transport — the IOptionsMonitor file-watcher
            // debounce may still be holding the previous values for a moment after the save. The
            // transport blocks the WTelegram thread, so run it off the UI thread.
            await Task.Run(() => _transport.EnsureConnectedAsync(snap, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return _transport.IsReady
                ? new TelegramArchiveLoginResult(true, "Connected.")
                : new TelegramArchiveLoginResult(false, "Login did not complete.");
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation("Telegram archive login canceled: {Reason}", ex.Message);
            return new TelegramArchiveLoginResult(false, $"Login canceled: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telegram archive login failed");
            return new TelegramArchiveLoginResult(false, $"Login failed: {ex.Message}");
        }
    }
}
