using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.IndexKScoreSurface;

public static class DependencyInjection
{
    public static IServiceCollection AddIndexKScoreSurfaceStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, IndexKScoreSurfaceStrategy>();
        services.AddTransient<IndexKScoreSurfaceViewModel>();
#if WINDOWS
        services.AddTransient<IndexKScoreSurfaceWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "index.kscore.surface",
            ViewFactory: sp => sp.GetRequiredService<IndexKScoreSurfaceWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<IndexKScoreSurfaceViewModel>()));
#else
        services.AddTransient<AvaloniaUi.IndexKScoreSurfaceAvaloniaWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "index.kscore.surface",
            ViewFactory: sp => sp.GetRequiredService<AvaloniaUi.IndexKScoreSurfaceAvaloniaWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<IndexKScoreSurfaceViewModel>()));
#endif

        return services;
    }
}
