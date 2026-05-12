using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.ThinBookFilter;

public static class DependencyInjection
{
    public static IServiceCollection AddThinBookFilterStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, ThinBookFilterStrategy>();
        services.AddTransient<ThinBookFilterStrategyViewModel>();
        services.AddTransient<ThinBookFilterStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "thin.book.filter",
            ViewFactory: sp => sp.GetRequiredService<ThinBookFilterStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<ThinBookFilterStrategyViewModel>()));
        return services;
    }
}