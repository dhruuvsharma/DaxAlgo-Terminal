using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.FilteredOrderFlow;

public static class DependencyInjection
{
    public static IServiceCollection AddFilteredOrderFlowStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, FilteredOrderFlowStrategy>();
        services.AddTransient<FilteredOrderFlowViewModel>();
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
