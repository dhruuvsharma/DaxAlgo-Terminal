using System.IO;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Backtest.Engine.Polyglot;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Infrastructure.Backtest;

namespace TradingTerminal.BacktestStudio;

/// <summary>DI registration for the Backtest Studio. Seeds the kernel registry from the engine's
/// native kernels, the 12 legacy strategies (adapter-wrapped via <see cref="LegacyKernelDescriptors"/>),
/// and any Python strategies discovered under <c>{app}/python-strategies/</c>, so the Studio catalog
/// shows everything. The VM/View are transient so each open gets a fresh run + playback timer.</summary>
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

            var pythonFolder = Path.Combine(AppContext.BaseDirectory, "python-strategies");
            descriptors.AddRange(PythonStrategyDescriptors.Discover(pythonFolder));

            return new StrategyKernelRegistry(descriptors);
        });
        services.AddTransient<BacktestStudioViewModel>();
#if WINDOWS
        services.AddTransient<BacktestStudioView>();
#endif
        return services;
    }
}
