using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Charts;

/// <summary>DI registration for the TradingView-style Charts tool. Transient so each open gets a
/// fresh VM that disposes with the window. Called once from <c>App.xaml.cs</c>.</summary>
public static class ChartsServiceCollectionExtensions
{
    public static IServiceCollection AddChartsSurface(this IServiceCollection services)
    {
        services.AddTransient<ChartsViewModel>();
        services.AddTransient<ChartsWindow>();
        return services;
    }
}
