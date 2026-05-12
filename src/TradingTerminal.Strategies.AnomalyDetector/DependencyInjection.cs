using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.AnomalyDetector;

public static class DependencyInjection
{
    public static IServiceCollection AddAnomalyDetectorStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, AnomalyDetectorStrategy>();
        services.AddTransient<AnomalyDetectorStrategyViewModel>();
        services.AddTransient<AnomalyDetectorStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "anomaly.detector",
            ViewFactory: sp => sp.GetRequiredService<AnomalyDetectorStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<AnomalyDetectorStrategyViewModel>()));
        return services;
    }
}