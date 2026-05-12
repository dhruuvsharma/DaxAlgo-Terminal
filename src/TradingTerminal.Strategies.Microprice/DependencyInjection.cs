using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.Microprice;

public static class DependencyInjection
{
    public static IServiceCollection AddMicropriceStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, MicropriceStrategy>();
        services.AddTransient<MicropriceStrategyViewModel>();
        services.AddTransient<MicropriceStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "microprice.deviation",
            ViewFactory: sp => sp.GetRequiredService<MicropriceStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<MicropriceStrategyViewModel>()));
        return services;
    }
}