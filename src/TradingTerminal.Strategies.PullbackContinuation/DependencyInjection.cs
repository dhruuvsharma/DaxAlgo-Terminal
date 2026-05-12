using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.Strategies.PullbackContinuation;

public static class DependencyInjection
{
    public static IServiceCollection AddPullbackContinuationStrategy(this IServiceCollection services)
    {
        services.AddSingleton<ITradingStrategy, PullbackContinuationStrategy>();
        services.AddTransient<PullbackContinuationStrategyViewModel>();
        services.AddTransient<PullbackContinuationStrategyWindow>();
        services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "pullback.continuation",
            ViewFactory: sp => sp.GetRequiredService<PullbackContinuationStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<PullbackContinuationStrategyViewModel>()));
        return services;
    }
}