using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Recording;

/// <summary>DI registration for the live tick recorder tab.</summary>
public static class RecordingServiceCollectionExtensions
{
    public static IServiceCollection AddRecordingSurface(this IServiceCollection services)
    {
        services.AddTransient<TickRecorderViewModel>();
        services.AddTransient<TickRecorderView>();
        return services;
    }
}
