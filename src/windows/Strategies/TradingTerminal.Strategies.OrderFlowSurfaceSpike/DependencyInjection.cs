using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OrderFlowSurfaceSpike;

public static class DependencyInjection
{
    public static IServiceCollection AddOrderFlowSurfaceSpikeStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, OrderFlowSurfaceSpikeStrategy>();
        services.AddTransient<OrderFlowSurfaceSpikeViewModel>();

        // Backtest entry — the plugin owns its engine (Engine/OrderFlowSurfaceSpikeStrategy) and
        // registers its own BacktestStrategyOption at runtime (no BacktestStrategyCatalog edit).
        services.AddSingleton(new BacktestStrategyOption(
            Id: "orderFlowSurfaceSpike",
            DisplayName: "Order-flow surface spike detector (Z-score over slice×bin matrix)",
            Build: contract => new Engine.OrderFlowSurfaceSpikeStrategy(contract))
        {
            DataRequirement = StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.TradeTape,
        });
#if WINDOWS
        services.AddTransient<OrderFlowSurfaceSpikeWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "orderflow.surface.spike",
            ViewFactory: sp => sp.GetRequiredService<OrderFlowSurfaceSpikeWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<OrderFlowSurfaceSpikeViewModel>()));
#else
        services.AddTransient<AvaloniaUi.OrderFlowSurfaceSpikeAvaloniaWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "orderflow.surface.spike",
            ViewFactory: sp => sp.GetRequiredService<AvaloniaUi.OrderFlowSurfaceSpikeAvaloniaWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<OrderFlowSurfaceSpikeViewModel>()));
#endif

        return services;
    }
}
