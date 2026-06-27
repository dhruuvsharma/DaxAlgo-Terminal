using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Ai.MlFeatures;

/// <summary>DI registration for the ML features tab (triple-barrier labelling + feature export).</summary>
public static class MlFeaturesServiceCollectionExtensions
{
    public static IServiceCollection AddMlFeatures(this IServiceCollection services)
    {
        services.AddTransient<MlFeaturesViewModel>();
#if WINDOWS
        services.AddTransient<MlFeaturesView>();
#endif
        return services;
    }
}
