using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.BubbleChart;

/// <summary>DI registration for the experimental Volume Bubble Line chart. Transient so each open
/// gets a fresh VM (+ trade subscription) that disposes with the window — exactly like the other
/// Charts-menu tools.</summary>
public static class BubbleChartServiceCollectionExtensions
{
    public static IServiceCollection AddBubbleChartSurface(this IServiceCollection services)
    {
        services.AddTransient<BubbleChartViewModel>();
        services.AddTransient<BubbleChartWindow>();
        return services;
    }
}
