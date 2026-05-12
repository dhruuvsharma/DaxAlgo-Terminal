using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.LondonOpenBreakout;

public static class DependencyInjection
{
    public static IServiceCollection AddLondonOpenBreakoutStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, LondonOpenBreakoutStrategy>();
        services.AddTransient<LondonOpenBreakoutStrategyViewModel>();
        services.AddTransient<LondonOpenBreakoutStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "london.open.breakout",
            ViewFactory: sp => sp.GetRequiredService<LondonOpenBreakoutStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<LondonOpenBreakoutStrategyViewModel>()));
        return services;
    }
}