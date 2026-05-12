using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.VolatilityTargeted;

public static class DependencyInjection
{
    public static IServiceCollection AddVolatilityTargetedStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, VolatilityTargetedStrategy>();
        services.AddTransient<VolatilityTargetedStrategyViewModel>();
        services.AddTransient<VolatilityTargetedStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "vol.targeted",
            ViewFactory: sp => sp.GetRequiredService<VolatilityTargetedStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<VolatilityTargetedStrategyViewModel>()));
        return services;
    }
}