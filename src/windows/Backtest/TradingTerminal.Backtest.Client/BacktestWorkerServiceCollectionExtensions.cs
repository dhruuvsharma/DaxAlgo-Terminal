using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Infrastructure.Backtest.Worker;

public static class BacktestWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddBacktestWorker(
        this IServiceCollection services,
        Action<BacktestWorkerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<BacktestWorkerOptions>();
        if (configure is not null) options.Configure(configure);
        services.AddSingleton<IBacktestJobClient, BacktestJobClient>();
        return services;
    }
}
