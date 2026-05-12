using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OnlineRegressionAlpha;

public static class DependencyInjection
{
    public static IServiceCollection AddOnlineRegressionAlphaStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, OnlineRegressionAlphaStrategy>();
        services.AddTransient<OnlineRegressionAlphaStrategyViewModel>();
        services.AddTransient<OnlineRegressionAlphaStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "online.regression.alpha",
            ViewFactory: sp => sp.GetRequiredService<OnlineRegressionAlphaStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<OnlineRegressionAlphaStrategyViewModel>()));
        return services;
    }
}