using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Ai.MarketAnalyst;

/// <summary>DI registration for the AI Market Analyst dock pane. The analyst client seam itself
/// (Null/Http) is registered separately by AddAiAnalyst in the shared TradingTerminal.Ai project.</summary>
public static class MarketAnalystServiceCollectionExtensions
{
    public static IServiceCollection AddMarketAnalyst(this IServiceCollection services)
    {
        services.AddTransient<AiAnalystViewModel>();
        services.AddTransient<AiAnalystView>();
        return services;
    }
}
