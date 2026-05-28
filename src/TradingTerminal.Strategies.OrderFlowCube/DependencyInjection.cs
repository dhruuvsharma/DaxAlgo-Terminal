using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OrderFlowCube;

public static class DependencyInjection
{
    public static IServiceCollection AddOrderFlowCubeStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, OrderFlowCubeStrategy>();
        services.AddTransient<OrderFlowCubeViewModel>();
        services.AddTransient<OrderFlowCubeWindow>();

        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "orderflow.cube",
            ViewFactory: sp => sp.GetRequiredService<OrderFlowCubeWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<OrderFlowCubeViewModel>()));

        return services;
    }
}
