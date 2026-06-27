using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Ai.BacktestAnalysis;

/// <summary>DI registration for the backtest analysis tab (walk-forward + Monte-Carlo).</summary>
public static class BacktestAnalysisServiceCollectionExtensions
{
    public static IServiceCollection AddBacktestAnalysis(this IServiceCollection services)
    {
        services.AddTransient<BacktestAnalysisViewModel>();
#if WINDOWS
        services.AddTransient<BacktestAnalysisView>();
#endif
        return services;
    }
}
