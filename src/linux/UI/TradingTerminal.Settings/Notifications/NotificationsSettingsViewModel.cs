using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Infrastructure.AiAnalyst;
using TradingTerminal.Infrastructure.Notifications;
using TradingTerminal.UI;

namespace TradingTerminal.App.Notifications;

/// <summary>
/// Backs the Notifications settings tab. Reads current values from <see cref="IOptionsMonitor{T}"/>,
/// lets the user edit a local copy, and on Save persists to the per-user JSON file. The
/// configuration system reloads automatically thanks to <c>reloadOnChange: true</c>.
/// </summary>
public sealed partial class NotificationsSettingsViewModel : ViewModelBase
{
    private readonly IOptionsMonitor<NotificationsOptions> _options;
    private readonly INotificationPublisher _publisher;
    private readonly ILogger<NotificationsSettingsViewModel> _logger;

    public NotificationsSettingsViewModel(
        IOptionsMonitor<NotificationsOptions> options,
        INotificationPublisher publisher,
        ILogger<NotificationsSettingsViewModel> logger)
    {
        _options = options;
        _publisher = publisher;
        _logger = logger;

        LoadFromOptions(options.CurrentValue);
    }

    [ObservableProperty] private bool _telegramEnabled;
    [ObservableProperty] private string _telegramBotToken = "";
    [ObservableProperty] private string _telegramChatId = "";
    [ObservableProperty] private bool _includeIdleSignals;

    [ObservableProperty] private bool _discordEnabled;
    [ObservableProperty] private string _discordWebhookUrl = "";
    [ObservableProperty] private string _discordUsername = "";
    [ObservableProperty] private bool _discordIncludeIdleSignals;

    [ObservableProperty] private bool _ollamaEnabled;
    [ObservableProperty] private string _ollamaEndpoint = "http://localhost:11434";
    [ObservableProperty] private string _ollamaModel = "llama3.2";
    [ObservableProperty] private int _ollamaTimeoutSeconds = 4;
    [ObservableProperty] private string _ollamaSystemPrompt = "";

    [ObservableProperty] private bool _aiAnalystEnabled;
    [ObservableProperty] private string _aiAnalystEndpoint = "http://127.0.0.1:8765";
    [ObservableProperty] private string _aiAnalystProvider = "openai";
    [ObservableProperty] private string _aiAnalystApiKey = "";
    [ObservableProperty] private string _aiAnalystModel = "gpt-4o";
    [ObservableProperty] private string _aiAnalystVisionModel = "gpt-4o";
    [ObservableProperty] private int _aiAnalystBarCount = 50;
    [ObservableProperty] private int _aiAnalystTimeoutSeconds = 60;
    [ObservableProperty] private bool _aiAnalystIncludeInEnricher;

    public IReadOnlyList<string> AiAnalystProviders { get; } =
        new[] { "openai", "anthropic", "qwen", "minimax", "gemini", "groq", "openrouter" };

    /// <summary>Per-provider default text/vision model ids, pre-filled when the user picks a provider so
    /// they don't have to know each backend's model naming. Gemini/Groq/OpenRouter default to free-tier
    /// ids. The user can still override either field afterwards.</summary>
    private static readonly IReadOnlyDictionary<string, (string Model, string VisionModel)> ProviderDefaults =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = ("gpt-4o", "gpt-4o"),
            ["anthropic"] = ("claude-opus-4-8", "claude-opus-4-8"),
            ["qwen"] = ("qwen-plus", "qwen-vl-plus"),
            ["minimax"] = ("abab6.5s-chat", "abab6.5s-chat"),
            ["gemini"] = ("gemini-2.0-flash", "gemini-2.0-flash"),
            ["groq"] = ("llama-3.3-70b-versatile", "meta-llama/llama-4-scout-17b-16e-instruct"),
            ["openrouter"] = ("google/gemini-2.0-flash-exp:free", "google/gemini-2.0-flash-exp:free"),
        };

    /// <summary>Suppresses <see cref="ProviderDefaults"/> pre-fill while hydrating from saved options,
    /// so loading a saved provider doesn't clobber the user's saved model ids.</summary>
    private bool _suppressProviderDefaults;

    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _hasUnsavedChanges;

    partial void OnTelegramEnabledChanged(bool value) => HasUnsavedChanges = true;
    partial void OnTelegramBotTokenChanged(string value) => HasUnsavedChanges = true;
    partial void OnTelegramChatIdChanged(string value) => HasUnsavedChanges = true;
    partial void OnIncludeIdleSignalsChanged(bool value) => HasUnsavedChanges = true;
    partial void OnDiscordEnabledChanged(bool value) => HasUnsavedChanges = true;
    partial void OnDiscordWebhookUrlChanged(string value) => HasUnsavedChanges = true;
    partial void OnDiscordUsernameChanged(string value) => HasUnsavedChanges = true;
    partial void OnDiscordIncludeIdleSignalsChanged(bool value) => HasUnsavedChanges = true;
    partial void OnOllamaEnabledChanged(bool value) => HasUnsavedChanges = true;
    partial void OnOllamaEndpointChanged(string value) => HasUnsavedChanges = true;
    partial void OnOllamaModelChanged(string value) => HasUnsavedChanges = true;
    partial void OnOllamaTimeoutSecondsChanged(int value) => HasUnsavedChanges = true;
    partial void OnOllamaSystemPromptChanged(string value) => HasUnsavedChanges = true;
    partial void OnAiAnalystEnabledChanged(bool value) => HasUnsavedChanges = true;
    partial void OnAiAnalystEndpointChanged(string value) => HasUnsavedChanges = true;
    partial void OnAiAnalystProviderChanged(string value)
    {
        HasUnsavedChanges = true;
        if (_suppressProviderDefaults) return;
        if (!string.IsNullOrWhiteSpace(value) && ProviderDefaults.TryGetValue(value.Trim(), out var d))
        {
            AiAnalystModel = d.Model;
            AiAnalystVisionModel = d.VisionModel;
        }
    }
    partial void OnAiAnalystApiKeyChanged(string value) => HasUnsavedChanges = true;
    partial void OnAiAnalystModelChanged(string value) => HasUnsavedChanges = true;
    partial void OnAiAnalystVisionModelChanged(string value) => HasUnsavedChanges = true;
    partial void OnAiAnalystBarCountChanged(int value) => HasUnsavedChanges = true;
    partial void OnAiAnalystTimeoutSecondsChanged(int value) => HasUnsavedChanges = true;
    partial void OnAiAnalystIncludeInEnricherChanged(bool value) => HasUnsavedChanges = true;

    private void LoadFromOptions(NotificationsOptions o)
    {
        _suppressProviderDefaults = true;
        TelegramEnabled = o.Telegram.Enabled;
        TelegramBotToken = o.Telegram.BotToken;
        TelegramChatId = o.Telegram.ChatId;
        IncludeIdleSignals = o.Telegram.IncludeIdleSignals;
        DiscordEnabled = o.Discord.Enabled;
        DiscordWebhookUrl = o.Discord.WebhookUrl;
        DiscordUsername = o.Discord.Username;
        DiscordIncludeIdleSignals = o.Discord.IncludeIdleSignals;
        OllamaEnabled = o.Ollama.Enabled;
        OllamaEndpoint = o.Ollama.Endpoint;
        OllamaModel = o.Ollama.Model;
        OllamaTimeoutSeconds = o.Ollama.TimeoutSeconds;
        OllamaSystemPrompt = o.Ollama.SystemPrompt;
        AiAnalystEnabled = o.AiAnalyst.Enabled;
        AiAnalystEndpoint = o.AiAnalyst.Endpoint;
        AiAnalystProvider = o.AiAnalyst.Provider;
        AiAnalystApiKey = o.AiAnalyst.ApiKey ?? "";
        AiAnalystModel = o.AiAnalyst.Model;
        AiAnalystVisionModel = o.AiAnalyst.VisionModel;
        AiAnalystBarCount = o.AiAnalyst.BarCount;
        AiAnalystTimeoutSeconds = o.AiAnalyst.TimeoutSeconds;
        AiAnalystIncludeInEnricher = o.AiAnalyst.IncludeInEnricher;
        HasUnsavedChanges = false;
        _suppressProviderDefaults = false;
    }

    [RelayCommand]
    private void Save()
    {
        var snapshot = _options.CurrentValue;
        var next = new NotificationsOptions
        {
            QueueCapacity = snapshot.QueueCapacity,
            Telegram = new TelegramOptions
            {
                Enabled = TelegramEnabled,
                BotToken = TelegramBotToken?.Trim() ?? "",
                ChatId = TelegramChatId?.Trim() ?? "",
                IncludeIdleSignals = IncludeIdleSignals,
            },
            Discord = new DiscordOptions
            {
                Enabled = DiscordEnabled,
                WebhookUrl = DiscordWebhookUrl?.Trim() ?? "",
                Username = DiscordUsername?.Trim() ?? "",
                IncludeIdleSignals = DiscordIncludeIdleSignals,
            },
            Ollama = new OllamaOptions
            {
                Enabled = OllamaEnabled,
                Endpoint = OllamaEndpoint?.Trim() ?? "",
                Model = OllamaModel?.Trim() ?? "",
                TimeoutSeconds = OllamaTimeoutSeconds,
                SystemPrompt = OllamaSystemPrompt ?? "",
            },
            AiAnalyst = new AiAnalystOptions
            {
                Enabled = AiAnalystEnabled,
                Endpoint = AiAnalystEndpoint?.Trim() ?? "",
                Provider = AiAnalystProvider?.Trim() ?? "openai",
                ApiKey = string.IsNullOrWhiteSpace(AiAnalystApiKey) ? null : AiAnalystApiKey,
                Model = AiAnalystModel?.Trim() ?? "",
                VisionModel = AiAnalystVisionModel?.Trim() ?? "",
                BarCount = Math.Max(1, AiAnalystBarCount),
                TimeoutSeconds = Math.Max(1, AiAnalystTimeoutSeconds),
                IncludeInEnricher = AiAnalystIncludeInEnricher,
            },
        };

        try
        {
            NotificationsUserFile.Save(next);
            HasUnsavedChanges = false;
            StatusMessage = $"Saved to {NotificationsUserFile.Path}";
            _logger.LogInformation("Notification settings saved");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
            _logger.LogError(ex, "Failed to save notification settings");
        }
    }

    [RelayCommand]
    private async Task SendTestAsync()
    {
        if (HasUnsavedChanges)
        {
            StatusMessage = "Save first — the test uses the saved configuration.";
            return;
        }
        var telegramReady = TelegramEnabled
            && !string.IsNullOrWhiteSpace(TelegramBotToken)
            && !string.IsNullOrWhiteSpace(TelegramChatId);
        var discordReady = DiscordEnabled && !string.IsNullOrWhiteSpace(DiscordWebhookUrl);
        if (!telegramReady && !discordReady)
        {
            StatusMessage = "No transport is configured.";
            return;
        }

        StatusMessage = "Sending…";
        await _publisher.PublishAsync(StrategyNotification.Test());
        StatusMessage = "Test queued. Check the configured channel (and the Logs pane for failures).";
    }
}
