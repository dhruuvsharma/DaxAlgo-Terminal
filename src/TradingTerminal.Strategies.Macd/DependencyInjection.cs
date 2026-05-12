using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.Macd;

public static class DependencyInjection
{
    public static IServiceCollection AddMacdStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, MacdStrategy>();
        services.AddTransient<MacdStrategyViewModel>();
        services.AddTransient<MacdStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "macd.crossover",
            ViewFactory: sp => sp.GetRequiredService<MacdStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<MacdStrategyViewModel>()));
        return services;
    }
}