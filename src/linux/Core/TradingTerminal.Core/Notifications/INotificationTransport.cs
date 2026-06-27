namespace TradingTerminal.Core.Notifications;

/// <summary>
/// One delivery channel (Telegram, Discord, Slack, email…). The dispatcher fans a
/// notification out to every transport that reports <see cref="IsEnabled"/> = true.
/// </summary>
public interface INotificationTransport
{
    /// <summary>Stable name for diagnostics ("telegram", "discord").</summary>
    string Name { get; }

    /// <summary>True when this transport has the configuration it needs to send.</summary>
    bool IsEnabled { get; }

    /// <summary>Send the message. May throw — the dispatcher logs and swallows.</summary>
    Task SendAsync(StrategyNotification notification, CancellationToken ct);
}
