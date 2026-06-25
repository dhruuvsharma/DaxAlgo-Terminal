using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.MarketData.AdvancedRegime;
using TradingTerminal.Infrastructure.Regime.AdvancedRegime;

namespace TradingTerminal.AdvancedMarketRegime;

/// <summary>DI registration for the Advanced Live Market Regime dashboard, including the
/// <see cref="IAdvancedRegimeProvider"/> implementation from Infrastructure. Transient panel so
/// each open gets a fresh view-model that disposes with the tab.</summary>
public static class AdvancedMarketRegimeServiceCollectionExtensions
{
    public static IServiceCollection AddAdvancedMarketRegimeSurface(this IServiceCollection services)
    {
        services.AddSingleton<IAdvancedRegimeProvider, AdvancedRegimeService>();
        services.AddTransient<AdvancedMarketRegimeViewModel>();
#if WINDOWS
        services.AddTransient<AdvancedMarketRegimeView>();
#endif
        return services;
    }
}
