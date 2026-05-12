using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.ConnorsRsi2;

public static class DependencyInjection
{
    public static IServiceCollection AddConnorsRsi2Strategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, ConnorsRsi2Strategy>();
        services.AddTransient<ConnorsRsi2StrategyViewModel>();
        services.AddTransient<ConnorsRsi2StrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "connors.rsi2",
            ViewFactory: sp => sp.GetRequiredService<ConnorsRsi2StrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<ConnorsRsi2StrategyViewModel>()));
        return services;
    }
}