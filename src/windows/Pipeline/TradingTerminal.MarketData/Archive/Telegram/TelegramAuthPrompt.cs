namespace TradingTerminal.Infrastructure.MarketData.Archive.Telegram;

/// <summary>
/// Bridge between the WTelegramClient login state machine (sync <c>Config</c> callback) and the
/// async UI flow that has to ask the user for the SMS code or 2FA password. The transport calls
/// <see cref="PromptAsync"/> with a key ("verification_code" / "password") and waits for the
/// user's reply; the UI fulfils it via the login dialog.
/// </summary>
public interface ITelegramAuthPrompt
{
    Task<string?> PromptAsync(string key, CancellationToken ct);
}

/// <summary>Trivial prompt that always returns null — used in headless contexts where the user
/// is expected to have a pre-existing session file.</summary>
public sealed class NullTelegramAuthPrompt : ITelegramAuthPrompt
{
    public Task<string?> PromptAsync(string key, CancellationToken ct) => Task.FromResult<string?>(null);
}
