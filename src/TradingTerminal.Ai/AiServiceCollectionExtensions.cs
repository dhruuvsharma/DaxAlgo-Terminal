using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.App.Ai;
using TradingTerminal.App.AiAnalyst;
using TradingTerminal.App.Research;
using TradingTerminal.Infrastructure.AiAnalyst;

namespace TradingTerminal.Ai;

/// <summary>
/// DI registration for all AI / ML tooling. The shell composition root calls <see cref="AddAi"/>
/// once; the App project never references the concrete analyst client, enricher, or AI tab
/// view-models directly.
/// </summary>
public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddAi(this IServiceCollection services, IConfiguration configuration)
    {
        // AI Analyst seam (hot-swappable Null ↔ Http via IOptionsMonitor) plus the notification
        // enricher that pipes analyst commentary into the alert pipeline.
        services.AddAiAnalyst(configuration);

        // AI Market Analyst dock pane.
        services.AddTransient<AiAnalystViewModel>();
        services.AddTransient<AiAnalystView>();

        // AI tools tabs — ML features (triple-barrier labelling) + Backtest analysis
        // (walk-forward + Monte Carlo).
        services.AddTransient<MlFeaturesViewModel>();
        services.AddTransient<MlFeaturesView>();
        services.AddTransient<BacktestAnalysisViewModel>();
        services.AddTransient<BacktestAnalysisView>();

        // Factor research notebook tab.
        services.AddTransient<FactorResearchViewModel>();
        services.AddTransient<FactorResearchView>();

        return services;
    }
}
