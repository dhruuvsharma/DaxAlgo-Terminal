using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OrderFlowSurfaceSpike;

public static class DependencyInjection
{
    public static IServiceCollection AddOrderFlowSurfaceSpikeStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, OrderFlowSurfaceSpikeStrategy>();
        services.AddTransient<OrderFlowSurfaceSpikeViewModel>();
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
