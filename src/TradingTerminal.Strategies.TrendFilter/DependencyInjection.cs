using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.TrendFilter;

public static class DependencyInjection
{
    public static IServiceCollection AddTrendFilterStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, TrendFilterStrategy>();
        services.AddTransient<TrendFilterStrategyViewModel>();
        services.AddTransient<TrendFilterStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "trend.filter",
            ViewFactory: sp => sp.GetRequiredService<TrendFilterStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<TrendFilterStrategyViewModel>()));
        return services;
    }
}