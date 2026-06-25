using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Ai.FactorResearch;

/// <summary>DI registration for the factor research notebook tab.</summary>
public static class FactorResearchServiceCollectionExtensions
{
    public static IServiceCollection AddFactorResearch(this IServiceCollection services)
    {
        services.AddTransient<FactorResearchViewModel>();
#if WINDOWS
        services.AddTransient<FactorResearchView>();
#endif
        return services;
    }
}
