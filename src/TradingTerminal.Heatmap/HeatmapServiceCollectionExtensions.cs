using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Heatmap;

/// <summary>DI registration for the Heatmap tools (depth, imbalance, volume-at-price, volatility,
/// rolling correlation). Transient so each open gets a fresh VM.</summary>
public static class HeatmapServiceCollectionExtensions
{
    public static IServiceCollection AddHeatmapSurface(this IServiceCollection services)
    {
        services.AddTransient<DepthHeatmapViewModel>();
        services.AddTransient<DepthHeatmapWindow>();
        services.AddTransient<ImbalanceHeatmapViewModel>();
        services.AddTransient<ImbalanceHeatmapWindow>();
        services.AddTransient<VolumeProfileHeatmapViewModel>();
        services.AddTransient<VolumeProfileHeatmapWindow>();
        services.AddTransient<VolumeBubbleHeatmapViewModel>();
        services.AddTransient<VolumeBubbleHeatmapWindow>();
        services.AddTransient<VolatilityHeatmapViewModel>();
        services.AddTransient<VolatilityHeatmapWindow>();
        services.AddTransient<CorrelationHeatmapViewModel>();
        services.AddTransient<CorrelationHeatmapWindow>();
        return services;
    }
}
