using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradingTerminal.Core.Ml;

namespace TradingTerminal.VolumeFootprint;

/// <summary>DI registration for the Volume Footprint tool. Transient so each open gets a fresh VM
/// (and trade subscription) that disposes with the window. Hosts may register an external batch
/// forecast provider before this module; otherwise the null provider leaves the online RLS fallback
/// active.</summary>
public static class VolumeFootprintServiceCollectionExtensions
{
    public static IServiceCollection AddFootprintSurface(this IServiceCollection services)
    {
        services.TryAddSingleton<IFootprintForecastProvider, NullFootprintForecastProvider>();
        services.AddTransient<VolumeFootprintViewModel>();
#if WINDOWS
        services.AddTransient<VolumeFootprintWindow>();
#endif
        return services;
    }
}
