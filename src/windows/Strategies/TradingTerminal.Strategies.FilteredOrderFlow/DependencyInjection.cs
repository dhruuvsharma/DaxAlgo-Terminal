using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.FilteredOrderFlow;

public static class DependencyInjection
{
    public static IServiceCollection AddFilteredOrderFlowStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, FilteredOrderFlowStrategy>();
        services.AddTransient<FilteredOrderFlowViewModel>();

        // Backtest entry — the plugin owns its engine (Engine/FilteredOrderFlowStrategy) and registers
        // its own BacktestStrategyOption at runtime (no BacktestStrategyCatalog edit).
        services.AddSingleton(new BacktestStrategyOption(
            Id: "filteredOrderFlow",
            DisplayName: "Filtered order-flow imbalance OBI(T) (arXiv:2507.22712)",
            Build: contract => new Engine.FilteredOrderFlowStrategy(contract))
        {
            DataRequirement = StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.TradeTape,
        });
#if WINDOWS
        services.AddTransient<FilteredOrderFlowWindow>();

        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "filtered.orderflow.imbalance",
            ViewFactory: sp => sp.GetRequiredService<FilteredOrderFlowWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<FilteredOrderFlowViewModel>()));
#else
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "filtered.orderflow.imbalance",
            ViewFactory: _ => new global::TradingTerminal.UI.Avalonia.GenericStrategyWindow(),
            ViewModelFactory: sp => sp.GetRequiredService<FilteredOrderFlowViewModel>()));
#endif

        return services;
    }
}
