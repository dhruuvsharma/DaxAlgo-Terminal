using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Infrastructure.Backtest;

namespace TradingTerminal.LseBacktest;

/// <summary>DI registration for the LSE Tools -> LSE backtester window. Shares the engine seam
/// (<see cref="IBacktestSession"/>) with the regular Backtest tool; transient lifetime so each
/// open gets a fresh session. The VM pulls its own data straight from the LSE broker client via
/// <c>IBrokerSelector</c>, so there is no data-path dependency here.</summary>
public static class LseBacktestServiceCollectionExtensions
{
    public static IServiceCollection AddLseBacktestSurface(this IServiceCollection services)
    {
        services.AddTransient<IBacktestSession, BacktestSession>();
        services.AddTransient<LseBacktestViewModel>();
#if WINDOWS
        services.AddTransient<LseBacktestView>();
#endif
        return services;
    }
}
