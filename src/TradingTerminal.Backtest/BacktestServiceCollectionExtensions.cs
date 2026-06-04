using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Infrastructure.Backtest;

namespace TradingTerminal.Backtest;

/// <summary>DI registration for the Backtest tab. <see cref="IBacktestSession"/> is the engine seam
/// so the VM stays testable; transient lifetime so each open of the tab gets a fresh session.</summary>
public static class BacktestServiceCollectionExtensions
{
    public static IServiceCollection AddBacktestSurface(this IServiceCollection services)
    {
        services.AddTransient<IBacktestSession, BacktestSession>();
        services.AddTransient<BacktestViewModel>();
        services.AddTransient<BacktestView>();
        return services;
    }
}
