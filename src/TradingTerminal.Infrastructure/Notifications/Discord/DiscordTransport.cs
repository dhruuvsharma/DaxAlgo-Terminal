using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Notifications;

namespace TradingTerminal.Infrastructure.Notifications.Discord;

/// <summary>
/// Posts to a Discord channel webhook. Reads options through IOptionsMonitor so the Settings
/// tab can edit them live. Mirrors <c>TelegramTransport</c>'s shape so new transports stay
/// boring.
/// </summary>
internal sealed class DiscordTransport : INotificationTransport
{
    public const string HttpClientName = "discord";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptionsMonitor<NotificationsOptions> _options;
    private readonly ILogger<DiscordTransport> _logger;

    public DiscordTransport(
        IHttpClientFactory httpFactory,
        IOptionsMonitor<NotificationsOptions> options,
        ILogger<DiscordTransport> logger)
    {
        _httpFactory = httpFactory;
        _options = options;
        _logger = logger;
    }

    public string Name => "discord";

    public bool IsEnabled
    {
        get
        {
            var d = _options.CurrentValue.Discord;
            return d.Enabled
                && !string.IsNullOrWhiteSpace(d.WebhookUrl)
                && Uri.TryCreate(d.WebhookUrl, UriKind.Absolute, out _);
        }
    }

    public async Task SendAsync(StrategyNotification notification, CancellationToken ct)
    {
        var d = _options.CurrentValue.Discord;

        if (notification.Kind == NotificationKind.IdleSignal && !d.IncludeIdleSignals)
            return;

        var http = _httpFactory.CreateClient(HttpClientName);
        var payload = new
        {
            content = NotificationFormatter.ToDiscordMarkdown(notification),
            username = string.IsNullOrWhiteSpace(d.Username) ? null : d.Username,
            allowed_mentions = new { parse = Array.Empty<string>() },
        };

        using var response = await http.PostAsJsonAsync(d.WebhookUrl, payload, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Discord send failed: {Status} {Body}", (int)response.StatusCode, body);
        }
    }
}
