using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.VolumeFootprint;

/// <summary>DI registration for the Volume Footprint tool. Transient so each open gets a fresh VM
/// (and trade subscription) that disposes with the window.</summary>
public static class VolumeFootprintServiceCollectionExtensions
{
    public static IServiceCollection AddFootprintSurface(this IServiceCollection services)
    {
        services.AddTransient<VolumeFootprintViewModel>();
#if WINDOWS
        services.AddTransient<VolumeFootprintWindow>();
#endif
        return services;
    }
}
