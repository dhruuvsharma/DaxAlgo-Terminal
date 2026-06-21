using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.BacktestStudio;

/// <summary>DI registration for the Backtest Studio. Seeds the kernel registry from the engine's
/// native kernels (adapter-wrapped legacy strategies are added here at integration time); the VM/View
/// are transient so each open gets a fresh run + playback timer.</summary>
public static class BacktestStudioServiceCollectionExtensions
{
    public static IServiceCollection AddBacktestStudioSurface(this IServiceCollection services)
    {
        services.AddSingleton<IStrategyKernelRegistry>(_ => new StrategyKernelRegistry(NativeKernels.All));
        services.AddTransient<BacktestStudioViewModel>();
        services.AddTransient<BacktestStudioView>();
        return services;
    }
}
