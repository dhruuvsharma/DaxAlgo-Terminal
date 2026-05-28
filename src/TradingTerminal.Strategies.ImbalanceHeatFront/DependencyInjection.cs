using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.ImbalanceHeatFront;

public static class DependencyInjection
{
    public static IServiceCollection AddImbalanceHeatFrontStrategy(this IServiceCollection services)
    {
        services.AddTransient<ImbalanceHeatFrontViewModel>();
        services.AddTransient<ImbalanceHeatFrontWindow>();

        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "imbalance.heatfront",
            ViewFactory: sp => sp.GetRequiredService<ImbalanceHeatFrontWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<ImbalanceHeatFrontViewModel>()));

        return services;
    }
}
