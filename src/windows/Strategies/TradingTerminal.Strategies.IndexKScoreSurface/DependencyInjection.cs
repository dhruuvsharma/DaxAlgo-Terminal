using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.IndexKScoreSurface;

public static class DependencyInjection
{
    public static IServiceCollection AddIndexKScoreSurfaceStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, IndexKScoreSurfaceStrategy>();
        services.AddTransient<IndexKScoreSurfaceViewModel>();
        services.AddTransient<IndexKScoreSurfaceWindow>();

        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "index.kscore.surface",
            ViewFactory: sp => sp.GetRequiredService<IndexKScoreSurfaceWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<IndexKScoreSurfaceViewModel>()));

        return services;
    }
}
