using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OrderFlowSurfaceSpike;

public static class DependencyInjection
{
    public static IServiceCollection AddOrderFlowSurfaceSpikeStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, OrderFlowSurfaceSpikeStrategy>();
        services.AddTransient<OrderFlowSurfaceSpikeViewModel>();
        services.AddTransient<OrderFlowSurfaceSpikeWindow>();

        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "orderflow.surface.spike",
            ViewFactory: sp => sp.GetRequiredService<OrderFlowSurfaceSpikeWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<OrderFlowSurfaceSpikeViewModel>()));

        return services;
    }
}
