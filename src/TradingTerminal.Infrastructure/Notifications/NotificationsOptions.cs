namespace TradingTerminal.Infrastructure.Notifications;

public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    public TelegramOptions Telegram { get; set; } = new();

    /// <summary>
    /// Bounded queue depth. When full, the oldest notification is dropped with a logged
    /// warning. Tunable in case a strategy gets chatty.
    /// </summary>
    public int QueueCapacity { get; set; } = 256;
}

public sealed class TelegramOptions
{
    public bool Enabled { get; set; }

    /// <summary>Bot token from @BotFather, of the shape "123456:AA…".</summary>
    public string BotToken { get; set; } = "";

    /// <summary>Chat ID — numeric for users/groups, "@channelname" for public channels.</summary>
    public string ChatId { get; set; } = "";

    /// <summary>If false, the transport drops <see cref="Core.Notifications.NotificationKind.IdleSignal"/> messages.</summary>
    public bool IncludeIdleSignals { get; set; }
}
