using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Infrastructure.Backtest;

namespace TradingTerminal.BacktestStudio;

/// <summary>DI registration for the Backtest Studio. Seeds the kernel registry from the engine's
/// native kernels plus the 12 legacy strategies (adapter-wrapped via <see cref="LegacyKernelDescriptors"/>),
/// so the Studio catalog shows everything. The VM/View are transient so each open gets a fresh run +
/// playback timer.</summary>
public static class BacktestStudioServiceCollectionExtensions
{
    public static IServiceCollection AddBacktestStudioSurface(this IServiceCollection services)
    {
        services.AddSingleton<IStrategyKernelRegistry>(sp =>
        {
            var descriptors = new List<StrategyKernelDescriptor>(NativeKernels.All);
            var legacy = sp.GetService<IBacktestStrategyRegistry>();
            if (legacy is not null)
            {
                var nativeIds = NativeKernels.All.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                descriptors.AddRange(LegacyKernelDescriptors.From(legacy, nativeIds));
            }
            return new StrategyKernelRegistry(descriptors);
        });
        services.AddTransient<BacktestStudioViewModel>();
        services.AddTransient<BacktestStudioView>();
        return services;
    }
}
