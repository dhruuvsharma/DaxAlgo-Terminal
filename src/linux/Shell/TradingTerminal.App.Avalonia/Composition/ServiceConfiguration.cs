using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TradingTerminal.App.Avalonia.Shell;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure;
using TradingTerminal.Infrastructure.AiAnalyst;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.MarketData;
using TradingTerminal.Infrastructure.MarketData.Archive;
using TradingTerminal.Infrastructure.Notifications;
using TradingTerminal.Infrastructure.Research;
using TradingTerminal.Strategies.CumulativeDelta;
using TradingTerminal.Strategies.FilteredOrderFlow;
using TradingTerminal.Strategies.ImbalanceHeatFront;
using TradingTerminal.Strategies.IndexKScoreSurface;
using TradingTerminal.Strategies.IndexRegimeGraph;
using TradingTerminal.Strategies.OrderFlowCube;
using TradingTerminal.Strategies.OrderFlowPressureMap;
using TradingTerminal.Strategies.OrderFlowSurfaceSpike;
using TradingTerminal.BacktestStudio;
using TradingTerminal.LseBacktest;
using TradingTerminal.QuantConnect;
using TradingTerminal.OrderBook;
using TradingTerminal.VolumeFootprint;
using TradingTerminal.Strategies.OrderFlowToxicity;
using TradingTerminal.Strategies.OrnsteinUhlenbeck;
using TradingTerminal.Strategies.SigmaIcFlow;
using TradingTerminal.Strategies.VolatilityTargeted;
using TradingTerminal.Core.Strategies;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;
using TradingTerminal.UI.Strategies;

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
        // Marshal repository/Paper-Lab UI work onto Avalonia's UI thread (overrides the headless
        // ImmediateUiDispatcher default registered by the pipeline; last registration wins).
        services.AddSingleton<TradingTerminal.Infrastructure.Threading.IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddNotifications(configuration);
        // AI analyst seam (IAiAnalystClient Null/Http, hot-swappable via NotificationsOptions).
        services.AddAiAnalyst(configuration);
        // Paper Lab research/repro seams (IPaperIngestClient/IReproOrchestrator Null defaults).
        services.AddPaperResearch(configuration);
        // Market-data archive (offloader + manifest store + Telegram transport). Uses the headless
        // NullTelegramAuthPrompt — the archive UI works; the Telegram verification-code modal is a
        // later Avalonia piece.
        services.AddMarketDataArchive(configuration);
        services.AddBacktestStrategyCatalog();

        // Strategy plug-in seam — the SAME factory the WPF shell uses. Every strategy resolves and
        // opens through IStrategyFactory.Create(id); the shell never names a concrete strategy. Each
        // strategy project registers a StrategyFactoryRegistration (Avalonia view) on its net9.0 leg.
        services.AddSingleton<IStrategyFactory, StrategyFactory>();

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
        services.AddFilteredOrderFlowStrategy();
        services.AddImbalanceHeatFrontStrategy();
        services.AddSigmaIcFlowStrategy();
        // Index Regime Graph consumes the Advanced Market Regime engine (Infrastructure, net9.0).
        services.TryAddSingleton<TradingTerminal.Core.MarketData.AdvancedRegime.IAdvancedRegimeProvider,
            TradingTerminal.Infrastructure.Regime.AdvancedRegime.AdvancedRegimeService>();
        services.AddIndexRegimeGraphStrategy();
        services.AddOrderFlowPressureMapStrategy();
        services.AddIndexKScoreSurfaceStrategy();
        services.AddOrderFlowCubeStrategy();
        services.AddOrderFlowSurfaceSpikeStrategy();

        // Shell view-models. The header API meter polls IBrokerApiMeter (registered by the
        // Infrastructure layer); MainWindowViewModel binds the catalog to IStrategyFactory.All.
        services.AddSingleton<BrokerApiMeterViewModel>();

        // AI tool VMs (portable — ILogger-only ctors; file I/O via the UiFile seam).
        services.AddTransient<TradingTerminal.Ai.MarketAnalyst.AiAnalystViewModel>();
        services.AddTransient<TradingTerminal.Ai.PaperLab.PaperLabViewModel>();

        // Settings/aux VMs (extracted to the shared TradingTerminal.Settings project — portable).
        services.AddTransient<TradingTerminal.App.Notifications.NotificationsSettingsViewModel>();
        services.AddTransient<TradingTerminal.App.Research.ResearchSettingsViewModel>();
        services.AddTransient<TradingTerminal.App.Support.SupportViewModel>();
        services.AddTransient<TradingTerminal.App.Authoring.StrategyAuthoringViewModel>();
        // Roslyn strategy compiler backs the authoring window.
        services.TryAddSingleton<TradingTerminal.Core.Strategies.Authoring.IStrategyCompiler,
            TradingTerminal.Infrastructure.Strategies.Authoring.RoslynStrategyCompiler>();
        services.AddTransient<TradingTerminal.App.Archive.ArchiveSettingsViewModel>();
        services.AddTransient<TradingTerminal.App.Archive.ArchiveActivityViewModel>();
        services.AddTransient<TradingTerminal.Ai.FactorResearch.FactorResearchViewModel>();
        services.AddTransient<TradingTerminal.Ai.MlFeatures.MlFeaturesViewModel>();
        services.AddTransient<TradingTerminal.Ai.BacktestAnalysis.BacktestAnalysisViewModel>();
        services.AddTransient<TradingTerminal.AdvancedMarketRegime.AdvancedMarketRegimeViewModel>();
        services.AddTransient<TradingTerminal.MarkovRegime.MarkovRegimeViewModel>();
        services.AddBacktestStudioSurface();
        services.AddTransient<TradingTerminal.Recording.TickRecorderViewModel>();
        services.AddLseBacktestSurface();
        services.AddTransient<TradingTerminal.Correlation.LiveCorrelationMatrixViewModel>();
        services.AddQuantConnectSurface(configuration);
        services.AddOrderBookSurface();
        services.AddFootprintSurface();

        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<Login.LoginViewModel>();

        return services.BuildServiceProvider();
    }
}
