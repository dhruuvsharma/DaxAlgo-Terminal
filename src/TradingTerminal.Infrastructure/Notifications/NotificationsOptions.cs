namespace TradingTerminal.Infrastructure.Notifications;

public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    public TelegramOptions Telegram { get; set; } = new();

    public DiscordOptions Discord { get; set; } = new();

    public OllamaOptions Ollama { get; set; } = new();

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

public sealed class OllamaOptions
{
    public bool Enabled { get; set; }

    /// <summary>Ollama HTTP base URL. Default Ollama listens on localhost:11434.</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>Model tag pulled in Ollama (e.g. "llama3.2", "phi3.5", "mistral"). Free, runs locally.</summary>
    public string Model { get; set; } = "llama3.2";

    /// <summary>
    /// Maximum wall-clock seconds the enricher will wait for the model to respond before
    /// falling through and dispatching the original notification. Keep low (≤ 5s) so a
    /// flaky local Ollama doesn't backlog the dispatcher.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 4;

    /// <summary>System prompt prepended to every request. Empty = use a default.</summary>
    public string SystemPrompt { get; set; } = "";
}

public sealed class DiscordOptions
{
    public bool Enabled { get; set; }

    /// <summary>Webhook URL from a Discord channel: Edit Channel → Integrations → Webhooks → New Webhook → Copy URL.</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>Optional username override displayed by the webhook. Empty = use the webhook's default.</summary>
    public string Username { get; set; } = "DaxAlgo Terminal";

    /// <summary>If false, the transport drops <see cref="Core.Notifications.NotificationKind.IdleSignal"/> messages.</summary>
    public bool IncludeIdleSignals { get; set; }
}
