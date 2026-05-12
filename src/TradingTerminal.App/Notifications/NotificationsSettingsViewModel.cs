using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Notifications;
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

    private void LoadFromOptions(NotificationsOptions o)
    {
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
        HasUnsavedChanges = false;
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
