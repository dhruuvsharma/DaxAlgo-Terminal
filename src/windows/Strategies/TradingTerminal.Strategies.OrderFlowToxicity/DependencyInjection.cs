using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OrderFlowToxicity;

public static class DependencyInjection
{
    public static IServiceCollection AddOrderFlowToxicityStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, OrderFlowToxicityStrategy>();
        services.AddTransient<OrderFlowToxicityStrategyViewModel>();
#if WINDOWS
        services.AddTransient<OrderFlowToxicityStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "order.flow.toxicity",
            ViewFactory: sp => sp.GetRequiredService<OrderFlowToxicityStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<OrderFlowToxicityStrategyViewModel>()));
#endif
        return services;
    }
}