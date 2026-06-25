using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.CumulativeDelta;

public static class DependencyInjection
{
    public static IServiceCollection AddCumulativeDeltaStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, CumulativeDeltaStrategy>();
        services.AddTransient<CumulativeDeltaViewModel>();
#if WINDOWS
        services.AddTransient<CumulativeDeltaWindow>();

        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "cumulative.delta.scalper",
            ViewFactory: sp => sp.GetRequiredService<CumulativeDeltaWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<CumulativeDeltaViewModel>()));
#else
        services.AddTransient<AvaloniaUi.CumulativeDeltaAvaloniaWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "cumulative.delta.scalper",
            ViewFactory: sp => sp.GetRequiredService<AvaloniaUi.CumulativeDeltaAvaloniaWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<CumulativeDeltaViewModel>()));
#endif

        return services;
    }
}
