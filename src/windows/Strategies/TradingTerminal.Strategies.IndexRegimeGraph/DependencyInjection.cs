using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.IndexRegimeGraph;

public static class DependencyInjection
{
    public static IServiceCollection AddIndexRegimeGraphStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, IndexRegimeGraphStrategy>();
        services.AddTransient<IndexRegimeGraphViewModel>();
#if WINDOWS
        services.AddTransient<IndexRegimeGraphWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "index.regime.graph",
            ViewFactory: sp => sp.GetRequiredService<IndexRegimeGraphWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<IndexRegimeGraphViewModel>()));
#else
        services.AddTransient<AvaloniaUi.IndexRegimeGraphAvaloniaWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "index.regime.graph",
            ViewFactory: sp => sp.GetRequiredService<AvaloniaUi.IndexRegimeGraphAvaloniaWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<IndexRegimeGraphViewModel>()));
#endif

        return services;
    }
}
