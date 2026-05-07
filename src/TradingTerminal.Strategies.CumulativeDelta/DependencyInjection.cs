using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.CumulativeDelta;

public static class DependencyInjection
{
    public static IServiceCollection AddCumulativeDeltaStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, CumulativeDeltaStrategy>();
        services.AddTransient<CumulativeDeltaViewModel>();
        services.AddTransient<CumulativeDeltaWindow>();

        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "cumulative.delta.scalper",
            ViewFactory: sp => sp.GetRequiredService<CumulativeDeltaWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<CumulativeDeltaViewModel>()));

        return services;
    }
}
