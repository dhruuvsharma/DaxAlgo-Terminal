using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.AvellanedaStoikov;

public static class DependencyInjection
{
    public static IServiceCollection AddAvellanedaStoikovStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, AvellanedaStoikovStrategy>();
        services.AddTransient<AvellanedaStoikovStrategyViewModel>();
        services.AddTransient<AvellanedaStoikovStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "avellaneda.stoikov",
            ViewFactory: sp => sp.GetRequiredService<AvellanedaStoikovStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<AvellanedaStoikovStrategyViewModel>()));
        return services;
    }
}