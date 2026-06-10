using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OrderFlowPressureMap;

public static class DependencyInjection
{
    public static IServiceCollection AddOrderFlowPressureMapStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, OrderFlowPressureMapStrategy>();
        services.AddTransient<OrderFlowPressureMapViewModel>();
        services.AddTransient<OrderFlowPressureMapWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "orderflow.pressuremap",
            ViewFactory: sp => sp.GetRequiredService<OrderFlowPressureMapWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<OrderFlowPressureMapViewModel>()));
        return services;
    }
}
