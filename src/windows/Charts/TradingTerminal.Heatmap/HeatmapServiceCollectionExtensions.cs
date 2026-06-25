using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Heatmap;

/// <summary>DI registration for the Heatmap surface — the single combined <b>Bookmap + VolBook</b>
/// window. Transient so each open gets a fresh VM.</summary>
public static class HeatmapServiceCollectionExtensions
{
    public static IServiceCollection AddHeatmapSurface(this IServiceCollection services)
    {
        services.AddTransient<BookmapHeatmapViewModel>();
#if WINDOWS
        services.AddTransient<BookmapHeatmapWindow>();
#endif
        return services;
    }
}
