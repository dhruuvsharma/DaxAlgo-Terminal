using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.MarkovRegime;

/// <summary>DI registration for the Markov regime detection tool — pure-C# Gaussian HMM (Core),
/// offline analysis over historical bars. Transient so each open gets a fresh VM.</summary>
public static class MarkovRegimeServiceCollectionExtensions
{
    public static IServiceCollection AddMarkovRegimeSurface(this IServiceCollection services)
    {
        services.AddTransient<MarkovRegimeViewModel>();
#if WINDOWS
        services.AddTransient<MarkovRegimeView>();
#endif
        return services;
    }
}
