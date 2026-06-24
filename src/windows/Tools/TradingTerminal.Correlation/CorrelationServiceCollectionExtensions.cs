using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Correlation;

/// <summary>DI registration for the Correlation Matrix tools (historical + live). Transient so each
/// open gets a fresh VM.</summary>
public static class CorrelationServiceCollectionExtensions
{
    public static IServiceCollection AddCorrelationSurface(this IServiceCollection services)
    {
        services.AddTransient<CorrelationMatrixViewModel>();
        services.AddTransient<CorrelationMatrixWindow>();
        services.AddTransient<LiveCorrelationMatrixViewModel>();
        services.AddTransient<LiveCorrelationMatrixWindow>();
        return services;
    }
}
