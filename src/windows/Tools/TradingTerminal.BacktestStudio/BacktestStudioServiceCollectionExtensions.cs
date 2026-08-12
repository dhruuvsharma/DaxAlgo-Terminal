using System.IO;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Backtest.Engine.Polyglot;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Backtest.Worker;

namespace TradingTerminal.BacktestStudio;

/// <summary>DI registration for the Backtest Studio. Keeps the kernel registry built-in-only, then
/// projects built-ins, runtime-authored strategies, Python kernels, and tracked DAXQ registrations
/// through <see cref="IStrategyCatalog"/>. The VM/View are transient so each open gets fresh state.</summary>
public static class BacktestStudioServiceCollectionExtensions
{
    public static IServiceCollection AddBacktestStudioSurface(this IServiceCollection services)
    {
        services.AddBacktestWorker();
        services.AddSingleton<ProtectedStrategyRegistrationSource>();
        services.AddSingleton<IProtectedStrategyRegistrationSource>(sp =>
            sp.GetRequiredService<ProtectedStrategyRegistrationSource>());
        ProtectedStrategyEngineDecoration.Install(services);

        services.AddSingleton<IStrategyKernelRegistry>(_ =>
        {
            var descriptors = new List<StrategyKernelDescriptor>(NativeKernels.All);
            var nativeIds = NativeKernels.All.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            descriptors.AddRange(LegacyKernelDescriptors.From(BacktestStrategyCatalog.All, nativeIds));
            return new StrategyKernelRegistry(descriptors);
        });
        services.AddSingleton<IStrategyCatalog>(sp =>
        {
            var pythonFolder = Path.Combine(AppContext.BaseDirectory, "python-strategies");
            var authoredKernels = PythonStrategyDescriptors.Discover(pythonFolder).ToArray();
            return new StrategyCatalog(
                sp.GetRequiredService<IStrategyKernelRegistry>(),
                sp.GetRequiredService<IBacktestStrategyRegistry>(),
                sp.GetRequiredService<IProtectedStrategyRegistrationSource>(),
                authoredKernels);
        });
        services.AddSingleton<BacktestStudioRunner>();
        services.AddTransient<BacktestStudioViewModel>();
#if WINDOWS
        services.AddTransient<BacktestStudioView>();
#endif
        return services;
    }
}
