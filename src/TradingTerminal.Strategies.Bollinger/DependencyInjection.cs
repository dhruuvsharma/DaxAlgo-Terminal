using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.Bollinger;

public static class DependencyInjection
{
    public static IServiceCollection AddBollingerStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, BollingerStrategy>();
        services.AddTransient<BollingerStrategyViewModel>();
        services.AddTransient<BollingerStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "bollinger.reversion",
            ViewFactory: sp => sp.GetRequiredService<BollingerStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<BollingerStrategyViewModel>()));
        return services;
    }
}