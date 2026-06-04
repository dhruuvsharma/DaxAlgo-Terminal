using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.MarketRegime;

/// <summary>DI registration for the Market Regime tab. The provider and refresh loop live in
/// Infrastructure (registered via AddMarketRegime in DependencyInjection); only the panel is here.
/// Transient so each open gets a fresh subscription that disposes with the tab.</summary>
public static class MarketRegimeServiceCollectionExtensions
{
    public static IServiceCollection AddMarketRegimeSurface(this IServiceCollection services)
    {
        services.AddTransient<MarketRegimeViewModel>();
        services.AddTransient<MarketRegimeView>();
        return services;
    }
}
