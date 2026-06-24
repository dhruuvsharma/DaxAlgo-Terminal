using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Ml.Stationarity;

/// <summary>DI registration for the Stationarity &amp; Differencing tool (Machine Learning menu) —
/// ADF/KPSS tests + differencing transforms (Core math), offline analysis over historical bars.
/// Transient so each open gets a fresh VM.</summary>
public static class StationarityServiceCollectionExtensions
{
    public static IServiceCollection AddStationaritySurface(this IServiceCollection services)
    {
        services.AddTransient<StationarityViewModel>();
        services.AddTransient<StationarityView>();
        return services;
    }
}
