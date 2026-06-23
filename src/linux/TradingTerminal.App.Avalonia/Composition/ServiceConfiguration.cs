using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.App.Avalonia.ViewModels;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.MarketData;
using TradingTerminal.Infrastructure.Notifications;
using TradingTerminal.Strategies.CumulativeDelta;
using TradingTerminal.Strategies.OrderFlowToxicity;
using TradingTerminal.Strategies.OrnsteinUhlenbeck;
using TradingTerminal.Strategies.VolatilityTargeted;
using TradingTerminal.UI;
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

        // Empty configuration — the headless services fall back to their defaults (SQLite store at
        // the default path, simulated broker, etc.). Real config files arrive when the shell grows up.
        IConfiguration configuration = new ConfigurationBuilder().Build();
        services.AddSingleton(configuration);
        services.AddLogging();

        // Universal Activity Log — one shared sink for the whole shell.
        services.AddSingleton<InMemoryLogSink>();

        // Headless pipeline + broker layer (WPF-free on net9.0) and the backtest strategy catalog.
        services.AddTradingTerminalInfrastructure();
        services.AddMarketDataPipeline(configuration);
        services.AddNotifications(configuration);
        services.AddBacktestStrategyCatalog();

        // Shared live-strategy plumbing (same bundle the WPF shell injects into every per-strategy VM).
        services.AddSingleton<ISignalGeneratorRouterFactory, SignalGeneratorRouterFactory>();
        services.AddSingleton(sp => new LiveStrategyHostServices(
            sp.GetRequiredService<IMarketDataRepository>(),
            sp.GetRequiredService<IMarketDataHub>(),
            sp.GetRequiredService<IMarketDataIngest>(),
            sp.GetRequiredService<IMarketDataStore>(),
            sp.GetRequiredService<IBrokerSelector>(),
            sp.GetRequiredService<InMemoryLogSink>()));

        // Ported per-strategy VMs (descriptor + portable VM; the WPF windows are #if'd out on net9.0).
        services.AddOrnsteinUhlenbeckStrategy();
        services.AddCumulativeDeltaStrategy();
        services.AddVolatilityTargetedStrategy();
        services.AddOrderFlowToxicityStrategy();

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
