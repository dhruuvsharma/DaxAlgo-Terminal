using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Infrastructure.Notifications.Discord;
using TradingTerminal.Infrastructure.Notifications.Ollama;
using TradingTerminal.Infrastructure.Notifications.Telegram;

namespace TradingTerminal.Infrastructure.Notifications;

public static class NotificationsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the notifications pipeline:
    ///   - <see cref="INotificationPublisher"/> (the dispatcher) as a singleton hosted service
    ///   - one <see cref="INotificationTransport"/> per supported channel (currently Telegram)
    ///   - HttpClient via IHttpClientFactory for the Telegram transport
    /// </summary>
    public static IServiceCollection AddNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NotificationsOptions>(configuration.GetSection(NotificationsOptions.SectionName));

        // Default signal gate (no-op). The market-regime module registers a real gate that
        // supersedes this; TryAdd keeps this as the fallback when that module isn't wired.
        services.TryAddSingleton<ISignalGate, AllowAllSignalGate>();

        services.AddSingleton<NotificationDispatcher>();
        services.AddSingleton<INotificationPublisher>(sp => sp.GetRequiredService<NotificationDispatcher>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<NotificationDispatcher>());

        services.AddHttpClient(TelegramTransport.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddSingleton<TelegramTransport>();
        services.AddSingleton<INotificationTransport>(sp => sp.GetRequiredService<TelegramTransport>());

        services.AddHttpClient(DiscordTransport.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddSingleton<DiscordTransport>();
        services.AddSingleton<INotificationTransport>(sp => sp.GetRequiredService<DiscordTransport>());

        services.AddHttpClient(OllamaCommentaryEnricher.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30); // outer ceiling; per-call timeout is set in the enricher
        });
        services.AddSingleton<INotificationEnricher, OllamaCommentaryEnricher>();

        return services;
    }
}
