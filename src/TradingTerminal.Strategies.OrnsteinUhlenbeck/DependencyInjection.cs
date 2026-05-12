using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.OrnsteinUhlenbeck;

public static class DependencyInjection
{
    public static IServiceCollection AddOrnsteinUhlenbeckStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, OrnsteinUhlenbeckStrategy>();
        services.AddTransient<OrnsteinUhlenbeckStrategyViewModel>();
        services.AddTransient<OrnsteinUhlenbeckStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "ornstein.uhlenbeck",
            ViewFactory: sp => sp.GetRequiredService<OrnsteinUhlenbeckStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<OrnsteinUhlenbeckStrategyViewModel>()));
        return services;
    }
}