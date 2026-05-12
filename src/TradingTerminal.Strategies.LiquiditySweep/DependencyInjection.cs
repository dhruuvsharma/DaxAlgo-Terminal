using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.LiquiditySweep;

public static class DependencyInjection
{
    public static IServiceCollection AddLiquiditySweepStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, LiquiditySweepStrategy>();
        services.AddTransient<LiquiditySweepStrategyViewModel>();
        services.AddTransient<LiquiditySweepStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "liquidity.sweep",
            ViewFactory: sp => sp.GetRequiredService<LiquiditySweepStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<LiquiditySweepStrategyViewModel>()));
        return services;
    }
}