using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.GapFade;

public static class DependencyInjection
{
    public static IServiceCollection AddGapFadeStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, GapFadeStrategy>();
        services.AddTransient<GapFadeStrategyViewModel>();
        services.AddTransient<GapFadeStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "gap.fade",
            ViewFactory: sp => sp.GetRequiredService<GapFadeStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<GapFadeStrategyViewModel>()));
        return services;
    }
}