using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.ImbalanceHeatFront;

public static class DependencyInjection
{
    public static IServiceCollection AddImbalanceHeatFrontStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, ImbalanceHeatFrontStrategy>();
        services.AddTransient<ImbalanceHeatFrontViewModel>();
#if WINDOWS
        services.AddTransient<ImbalanceHeatFrontWindow>();

        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "imbalance.heatfront",
            ViewFactory: sp => sp.GetRequiredService<ImbalanceHeatFrontWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<ImbalanceHeatFrontViewModel>()));
#else
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "imbalance.heatfront",
            ViewFactory: _ => new global::TradingTerminal.UI.Avalonia.GenericStrategyWindow(),
            ViewModelFactory: sp => sp.GetRequiredService<ImbalanceHeatFrontViewModel>()));
#endif

        return services;
    }
}
