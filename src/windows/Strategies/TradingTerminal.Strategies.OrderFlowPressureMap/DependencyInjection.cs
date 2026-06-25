using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OrderFlowPressureMap;

public static class DependencyInjection
{
    public static IServiceCollection AddOrderFlowPressureMapStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, OrderFlowPressureMapStrategy>();
        services.AddTransient<OrderFlowPressureMapViewModel>();
#if WINDOWS
        services.AddTransient<OrderFlowPressureMapWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "orderflow.pressuremap",
            ViewFactory: sp => sp.GetRequiredService<OrderFlowPressureMapWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<OrderFlowPressureMapViewModel>()));
#else
        services.AddTransient<AvaloniaUi.OrderFlowPressureMapAvaloniaWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "orderflow.pressuremap",
            ViewFactory: sp => sp.GetRequiredService<AvaloniaUi.OrderFlowPressureMapAvaloniaWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<OrderFlowPressureMapViewModel>()));
#endif
        return services;
    }
}
