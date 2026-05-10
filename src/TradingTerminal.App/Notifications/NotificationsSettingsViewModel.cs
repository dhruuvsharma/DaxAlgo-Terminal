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

    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _hasUnsavedChanges;

    partial void OnTelegramEnabledChanged(bool value) => HasUnsavedChanges = true;
    partial void OnTelegramBotTokenChanged(string value) => HasUnsavedChanges = true;
    partial void OnTelegramChatIdChanged(string value) => HasUnsavedChanges = true;
    partial void OnIncludeIdleSignalsChanged(bool value) => HasUnsavedChanges = true;

    private void LoadFromOptions(NotificationsOptions o)
    {
        TelegramEnabled = o.Telegram.Enabled;
        TelegramBotToken = o.Telegram.BotToken;
        TelegramChatId = o.Telegram.ChatId;
        IncludeIdleSignals = o.Telegram.IncludeIdleSignals;
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
        if (!TelegramEnabled || string.IsNullOrWhiteSpace(TelegramBotToken) || string.IsNullOrWhiteSpace(TelegramChatId))
        {
            StatusMessage = "Telegram is not configured.";
            return;
        }

        StatusMessage = "Sending…";
        await _publisher.PublishAsync(StrategyNotification.Test());
        StatusMessage = "Test queued. Check Telegram (and the Logs pane for failures).";
    }
}
