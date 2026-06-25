using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Ai.PaperLab;

/// <summary>
/// DI registration for the Paper Lab tool window. The paper-research backend seams
/// (<see cref="TradingTerminal.Core.Research.IPaperIngestClient"/>,
/// <see cref="TradingTerminal.Core.Research.IReproOrchestrator"/>, the SQLite job store,
/// the sandbox runner) are registered separately by <c>AddPaperResearch</c> in
/// <c>TradingTerminal.Infrastructure</c>. Call both from <c>App.xaml.cs</c>.
/// </summary>
public static class PaperLabServiceCollectionExtensions
{
    /// <summary>Register the Paper Lab view and view-model as transient services. Mirrors
    /// <c>MarketAnalystServiceCollectionExtensions.AddMarketAnalyst()</c>.</summary>
    public static IServiceCollection AddPaperLab(this IServiceCollection services)
    {
        services.AddTransient<PaperLabViewModel>();
#if WINDOWS
        services.AddTransient<PaperLabView>();
#endif
        return services;
    }
}
