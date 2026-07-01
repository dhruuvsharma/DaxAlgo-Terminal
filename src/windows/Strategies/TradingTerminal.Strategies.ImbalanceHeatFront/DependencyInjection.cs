using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.ImbalanceHeatFront;

public static class DependencyInjection
{
    public static IServiceCollection AddImbalanceHeatFrontStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, ImbalanceHeatFrontStrategy>();
        services.AddTransient<ImbalanceHeatFrontViewModel>();

        // Backtest entry — the plugin owns its engine (Engine/ImbalanceHeatFrontStrategy) and registers
        // its own BacktestStrategyOption at runtime (no BacktestStrategyCatalog edit).
        services.AddSingleton(new BacktestStrategyOption(
            Id: "imbalanceHeatFront",
            DisplayName: "Imbalance Heat Front (L2 bid-ask pressure surface)",
            Build: contract => new Engine.ImbalanceHeatFrontStrategy(contract))
        {
            DataRequirement = StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.Depth,
        });
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
