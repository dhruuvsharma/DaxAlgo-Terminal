using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.ApexScalper;

public static class DependencyInjection
{
    public static IServiceCollection AddApexScalperStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, ApexScalperStrategy>();
        services.AddTransient<ApexScalperStrategyViewModel>();
        services.AddTransient<ApexScalperStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "apex.scalper",
            ViewFactory: sp => sp.GetRequiredService<ApexScalperStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<ApexScalperStrategyViewModel>()));
        return services;
    }
}
