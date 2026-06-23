using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.App.Avalonia.ViewModels;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.UI.Catalog;
using TradingTerminal.UI.Logging;

namespace TradingTerminal.App.Avalonia.Composition;

/// <summary>
/// Composition root for the Avalonia shell. Mirrors the WPF App's MS.DI host but with the headless,
/// cross-platform slice only — no WPF services. As more windows are ported, register their
/// (portable) view-models here. Kept a plain <see cref="ServiceCollection"/> for now; it can grow
/// into a full Generic Host (config + Serilog) when the shell needs settings/logging files.
/// </summary>
public static class ServiceConfiguration
{
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        // Universal Activity Log — one shared sink for the whole shell.
        services.AddSingleton<InMemoryLogSink>();

        // Headless backtest strategy catalog (the runtime source of strategies).
        services.AddBacktestStrategyCatalog();

        // Portable view-models (shared with the WPF shell).
        services.AddSingleton<StrategyCatalogViewModel>(sp =>
        {
            var registry = sp.GetRequiredService<IBacktestStrategyRegistry>();
            var log = sp.GetRequiredService<InMemoryLogSink>();
            return new StrategyCatalogViewModel(registry.All, msg => log.Append("Catalog", "INFO", msg));
        });
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
