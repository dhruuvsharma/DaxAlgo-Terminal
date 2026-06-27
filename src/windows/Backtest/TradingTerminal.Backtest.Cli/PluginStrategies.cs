using DaxAlgo.Sdk;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Plugins;

namespace TradingTerminal.Backtest.Cli;

/// <summary>
/// Loads strategy plugins from <c>{exeDir}/plugins</c> (the same convention as the WPF app) and
/// exposes their backtest strategies to the CLI's resolver — so the headless backtester can run
/// plugin-provided strategies (e.g. <c>sigmaIcFlow</c> once it ships as a plugin, or any third-party
/// plugin) without compile-referencing them. Loaded lazily, once, on first strategy lookup.
/// </summary>
internal static class PluginStrategies
{
    private static readonly Lazy<IReadOnlyList<BacktestStrategyOption>> Loaded = new(Load);

    /// <summary>Backtest options contributed by all loaded plugins.</summary>
    public static IReadOnlyList<BacktestStrategyOption> Options => Loaded.Value;

    /// <summary>Ids of all plugin-provided backtest strategies (for the "unknown strategy" message).</summary>
    public static IReadOnlyList<string> AvailableIds => Options.Select(o => o.Id).ToArray();

    /// <summary>Builds the plugin strategy with id <paramref name="id"/> (case-insensitive), honouring
    /// any backtest preset, or returns <c>null</c> when no plugin provides it.</summary>
    public static IBacktestStrategy? TryCreate(string id, Contract contract)
    {
        var option = Options.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
        return option?.CreateForBacktest(contract);
    }

    private static IReadOnlyList<BacktestStrategyOption> Load()
    {
        var services = new ServiceCollection();
        var pluginsRoot = Path.Combine(AppContext.BaseDirectory, "plugins");

        var loaded = PluginLoader.LoadInto(services, pluginsRoot, SdkInfo.Version,
            onError: (path, ex) => Console.Error.WriteLine($"warning: skipped plugin '{path}': {ex.Message}"));
        foreach (var plugin in loaded)
            Console.Error.WriteLine($"Loaded plugin '{plugin.Name}' (DaxAlgo.Sdk {plugin.TargetSdkVersion}).");

        // Each plugin registers its BacktestStrategyOption as a singleton instance; collect them
        // straight off the descriptors (no provider build needed).
        return services
            .Where(d => d.ServiceType == typeof(BacktestStrategyOption) && d.ImplementationInstance is BacktestStrategyOption)
            .Select(d => (BacktestStrategyOption)d.ImplementationInstance!)
            .ToList();
    }
}
