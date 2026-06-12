using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Ml.ArimaGarch;

/// <summary>DI registration for the ARIMA &amp; GARCH tool (Machine Learning menu) — price
/// forecasting + conditional-volatility modelling (Core math), offline analysis over historical
/// bars. Transient so each open gets a fresh VM.</summary>
public static class ArimaGarchServiceCollectionExtensions
{
    public static IServiceCollection AddArimaGarchSurface(this IServiceCollection services)
    {
        services.AddTransient<ArimaGarchViewModel>();
        services.AddTransient<ArimaGarchView>();
        return services;
    }
}
