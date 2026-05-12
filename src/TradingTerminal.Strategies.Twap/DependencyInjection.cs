using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.Twap;

public static class DependencyInjection
{
    public static IServiceCollection AddTwapStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, TwapStrategy>();
        services.AddTransient<TwapStrategyViewModel>();
        services.AddTransient<TwapStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "twap.execution",
            ViewFactory: sp => sp.GetRequiredService<TwapStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<TwapStrategyViewModel>()));
        return services;
    }
}