using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Correlation;

/// <summary>DI registration for the Correlation Matrix tools (historical + live). Transient so each
/// open gets a fresh VM.</summary>
public static class CorrelationServiceCollectionExtensions
{
    public static IServiceCollection AddCorrelationSurface(this IServiceCollection services)
    {
        services.AddTransient<CorrelationMatrixViewModel>();
        services.AddTransient<LiveCorrelationMatrixViewModel>();
#if WINDOWS
        services.AddTransient<CorrelationMatrixWindow>();
        services.AddTransient<LiveCorrelationMatrixWindow>();
#endif
        return services;
    }
}
