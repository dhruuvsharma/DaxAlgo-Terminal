using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Recording;

/// <summary>DI registration for the live market-data recorder.</summary>
public static class RecordingServiceCollectionExtensions
{
    public static IServiceCollection AddRecordingSurface(this IServiceCollection services)
    {
        // One instance, two roles: the app-lifetime recorder (so recording survives the panel closing)
        // and a hosted service (so the watchlist loads at start and pumps stop cleanly at shutdown).
        services.AddSingleton<TickRecordingService>();
        services.AddHostedService(sp => sp.GetRequiredService<TickRecordingService>());

        services.AddTransient<RecorderPanelViewModel>();
#if WINDOWS
        services.AddTransient<RecorderPanelView>();
#endif
        return services;
    }
}
