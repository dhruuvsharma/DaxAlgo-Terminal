using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Notifications;

namespace TradingTerminal.Infrastructure.Notifications.Telegram;

/// <summary>
/// Posts to api.telegram.org/bot{token}/sendMessage. Reads options through IOptionsMonitor
/// so changes from the Settings tab take effect without restart.
/// </summary>
internal sealed class TelegramTransport : INotificationTransport
{
    public const string HttpClientName = "telegram";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<NotificationsOptions> _options;
    private readonly ILogger<TelegramTransport> _logger;

    public TelegramTransport(
        IHttpClientFactory httpFactory,
        IOptionsMonitor<NotificationsOptions> options,
        ILogger<TelegramTransport> logger)
    {
        _httpFactory = httpFactory;
        _options = options;
        _logger = logger;
    }

    public string Name => "telegram";

    public bool IsEnabled
    {
        get
        {
            var t = _options.CurrentValue.Telegram;
            return t.Enabled
                && !string.IsNullOrWhiteSpace(t.BotToken)
                && !string.IsNullOrWhiteSpace(t.ChatId);
        }
    }

    public async Task SendAsync(StrategyNotification notification, CancellationToken ct)
    {
        var t = _options.CurrentValue.Telegram;

        if (notification.Kind == NotificationKind.IdleSignal && !t.IncludeIdleSignals)
            return;

        var http = _httpFactory.CreateClient(HttpClientName);
        var url = $"https://api.telegram.org/bot{t.BotToken}/sendMessage";
        var payload = new
        {
            chat_id = t.ChatId,
            text = NotificationFormatter.ToTelegramHtml(notification),
            parse_mode = "HTML",
            disable_web_page_preview = true,
        };

        using var response = await http.PostAsJsonAsync(url, payload, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Telegram send failed: {Status} {Body}", (int)response.StatusCode, body);
        }
    }
}
