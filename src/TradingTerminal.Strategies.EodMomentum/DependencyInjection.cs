using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.EodMomentum;

public static class DependencyInjection
{
    public static IServiceCollection AddEodMomentumStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, EodMomentumStrategy>();
        services.AddTransient<EodMomentumStrategyViewModel>();
        services.AddTransient<EodMomentumStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "eod.momentum",
            ViewFactory: sp => sp.GetRequiredService<EodMomentumStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<EodMomentumStrategyViewModel>()));
        return services;
    }
}