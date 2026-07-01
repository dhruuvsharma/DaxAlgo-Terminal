using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OrderFlowCube;

public static class DependencyInjection
{
    public static IServiceCollection AddOrderFlowCubeStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, OrderFlowCubeStrategy>();
        services.AddTransient<OrderFlowCubeViewModel>();

        // Backtest entry — the plugin owns its engine (Engine/OrderFlowCubeStrategy, moved in from
        // Infrastructure) and registers its own BacktestStrategyOption at runtime; the host's
        // IBacktestStrategyRegistry aggregates it with no BacktestStrategyCatalog edit.
        services.AddSingleton(new BacktestStrategyOption(
            Id: "orderFlowCube",
            DisplayName: "Order-flow regime cube (CVD × aggressor × size)",
            Build: contract => new Engine.OrderFlowCubeStrategy(contract))
        {
            DataRequirement = StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.TradeTape,
        });
#if WINDOWS
        services.AddTransient<OrderFlowCubeWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "orderflow.cube",
            ViewFactory: sp => sp.GetRequiredService<OrderFlowCubeWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<OrderFlowCubeViewModel>()));
#else
        services.AddTransient<AvaloniaUi.OrderFlowCubeAvaloniaWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "orderflow.cube",
            ViewFactory: sp => sp.GetRequiredService<AvaloniaUi.OrderFlowCubeAvaloniaWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<OrderFlowCubeViewModel>()));
#endif

        return services;
    }
}
