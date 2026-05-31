using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.AiAnalyst;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Infrastructure.Notifications;
using TradingTerminal.Infrastructure.Notifications.AiAnalyst;

namespace TradingTerminal.Infrastructure.AiAnalyst;

public static class AiAnalystServiceCollectionExtensions
{
    /// <summary>
    /// Registers the AI Analyst seam. The single registered <see cref="IAiAnalystClient"/>
    /// is a dispatching wrapper that reads <c>NotificationsOptions.AiAnalyst.Enabled</c>
    /// on every call, so flipping the toggle from Settings hot-swaps Null ↔ Http without
    /// a restart.
    /// </summary>
    public static IServiceCollection AddAiAnalyst(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(HttpAiAnalystClient.HttpClientName, c =>
        {
            // Outer ceiling; the per-call timeout lives in AiAnalystOptions.TimeoutSeconds.
            c.Timeout = TimeSpan.FromMinutes(2);
        });

        services.AddSingleton<NullAiAnalystClient>();
        services.AddSingleton<HttpAiAnalystClient>();
        services.AddSingleton<IAiAnalystClient, DispatchingAiAnalystClient>();

        // Registered AFTER the Ollama enricher (which lives in AddNotifications) because
        // App.xaml.cs calls AddNotifications before AddAiAnalyst. The dispatcher fans every
        // notification through both enrichers in registration order; they're independent
        // and either may pass through unchanged.
        services.AddSingleton<INotificationEnricher, AiAnalystEnricher>();

        return services;
    }

    private sealed class DispatchingAiAnalystClient : IAiAnalystClient
    {
        private readonly IOptionsMonitor<NotificationsOptions> _options;
        private readonly NullAiAnalystClient _null;
        private readonly HttpAiAnalystClient _http;

        public DispatchingAiAnalystClient(
            IOptionsMonitor<NotificationsOptions> options,
            NullAiAnalystClient nullClient,
            HttpAiAnalystClient httpClient)
        {
            _options = options;
            _null = nullClient;
            _http = httpClient;
        }

        public bool IsAvailable => _options.CurrentValue.AiAnalyst.Enabled;

        public Task<AnalystReport> RunAsync(AnalystRequest request, CancellationToken ct = default) =>
            _options.CurrentValue.AiAnalyst.Enabled
                ? _http.RunAsync(request, ct)
                : _null.RunAsync(request, ct);
    }
}
