using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.Rsi;

public static class DependencyInjection
{
    public static IServiceCollection AddRsiStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, RsiStrategy>();
        services.AddTransient<RsiStrategyViewModel>();
        services.AddTransient<RsiStrategyWindow>();

        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "rsi.overbought.oversold",
            ViewFactory: sp => sp.GetRequiredService<RsiStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<RsiStrategyViewModel>()));

        return services;
    }
}
