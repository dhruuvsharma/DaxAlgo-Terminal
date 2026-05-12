using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.MaCrossover;

public static class DependencyInjection
{
    public static IServiceCollection AddMaCrossoverStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, MaCrossoverStrategy>();
        services.AddTransient<MaCrossoverStrategyViewModel>();
        services.AddTransient<MaCrossoverStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "ma.crossover",
            ViewFactory: sp => sp.GetRequiredService<MaCrossoverStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<MaCrossoverStrategyViewModel>()));
        return services;
    }
}