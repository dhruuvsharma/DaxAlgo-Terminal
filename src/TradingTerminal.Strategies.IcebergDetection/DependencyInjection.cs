using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.IcebergDetection;

public static class DependencyInjection
{
    public static IServiceCollection AddIcebergDetectionStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, IcebergDetectionStrategy>();
        services.AddTransient<IcebergDetectionStrategyViewModel>();
        services.AddTransient<IcebergDetectionStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "iceberg.detection",
            ViewFactory: sp => sp.GetRequiredService<IcebergDetectionStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<IcebergDetectionStrategyViewModel>()));
        return services;
    }
}