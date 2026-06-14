using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.IndexRegimeGraph;

public static class DependencyInjection
{
    public static IServiceCollection AddIndexRegimeGraphStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, IndexRegimeGraphStrategy>();
        services.AddTransient<IndexRegimeGraphViewModel>();
        services.AddTransient<IndexRegimeGraphWindow>();

        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "index.regime.graph",
            ViewFactory: sp => sp.GetRequiredService<IndexRegimeGraphWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<IndexRegimeGraphViewModel>()));

        return services;
    }
}
