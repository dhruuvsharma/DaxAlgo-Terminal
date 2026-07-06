using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradingTerminal.App.Archive;
using TradingTerminal.App.Notifications;
using TradingTerminal.App.Shell;
using TradingTerminal.Core.Strategies;
using TradingTerminal.UI.Strategies;
using TradingTerminal.UI.Logging;
using TradingTerminal.UI.Theming;
using TradingTerminal.Infrastructure.AiAnalyst;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Backtest.Fast;
using TradingTerminal.Infrastructure.MarketData;
using TradingTerminal.Infrastructure.MarketData.Archive;
using TradingTerminal.Infrastructure.MarketData.Archive.Lake;
using TradingTerminal.Infrastructure.Notifications;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Regime;
using TradingTerminal.UI;
// Common per-tool surfaces (Add…Surface extensions) — every edition ships these.
using TradingTerminal.Login;
using TradingTerminal.Charts;
using TradingTerminal.OrderBook;
using TradingTerminal.VolumeFootprint;
using TradingTerminal.Heatmap;
using TradingTerminal.Correlation;
using TradingTerminal.Backtest;
using TradingTerminal.BacktestStudio;
using TradingTerminal.Recording;
using TradingTerminal.AdvancedMarketRegime;

namespace TradingTerminal.App.Composition;

/// <summary>
/// Per-feature DI extension methods used by <c>App.xaml.cs</c>. Splitting these out turns
/// the composition root into a short, readable manifest — each line is a feature module.
/// Adding a new module: write a new <c>AddXxx</c> method here and call it from
/// <c>App.OnStartup</c>.
/// </summary>
public static class AppDependencyInjection
{
    /// <summary>
    /// The COMMON composition shared by every edition shell — the canonical market-data pipeline,
    /// archive, Parquet lake, notifications, market regime, strategy plug-ins, login, the shell +
    /// window host, support/settings, the AI-analyst seam (Null client by default; the notification
    /// enricher depends on it, so it ships in every edition), and the common tool/chart surfaces
    /// (Backtest / Backtest Studio / Recorder / Correlation / Charts / Order book / Footprint /
    /// Bookmap+Heatmap / Advanced market regime), plus the cross-cutting singletons.
    ///
    /// A per-edition <c>App.xaml.cs</c> calls this after registering the edition (<see cref="AppEdition"/>)
    /// and the broker layer, then layers its tier-exclusive surfaces on top.
    /// </summary>
    public static IServiceCollection AddCoreShell(this IServiceCollection services, IConfiguration configuration)
    {
        // Cross-cutting singletons. The InMemoryLogSink instance is normally registered by the shell
        // bootstrap first (it must be the same instance the Serilog sink writes to), so TryAdd here is
        // a no-op in that case and a safety net otherwise.
        services.TryAddSingleton<InMemoryLogSink>();
        services.TryAddSingleton<IThemeManager, ThemeManager>();

        // Canonical pipeline + archive + parquet + notifications + regime.
        services.AddMarketDataPipeline(configuration);
        services.AddMarketDataArchive(configuration);
        services.AddParquetLake(configuration);
        services.AddNotifications(configuration);
        // Market regime — registered after AddNotifications so its risk-off signal gate supersedes the
        // notifications module's no-op default.
        services.AddMarketRegime(configuration);

        // Python-sidecar controller seam. The login screen depends on ISidecarController, but the real
        // sidecar host is a Professional-only surface (AddSidecar). Register a no-op fallback here so
        // Basic/Intermediate compose; Professional's AddSidecar (a plain AddSingleton) registers the real
        // one after this and wins.
        services.TryAddSingleton<TradingTerminal.Core.Hosting.ISidecarController,
            TradingTerminal.Core.Hosting.NullSidecarController>();

        // Feature modules common to every edition.
        services.AddStrategyPlugins();
        services.AddLogin();
        services.AddShell();
        services.AddSupport();
        services.AddSettingsSurface();
        services.AddArchiveSurface();

        // Common tool/chart surfaces.
        services.AddBacktestSurface();
        services.AddBacktestStudioSurface();
        services.AddRecordingSurface();
        services.AddCorrelationSurface();
        services.AddChartsSurface();
        services.AddOrderBookSurface();
        services.AddFootprintSurface();
        services.AddHeatmapSurface();
        services.AddAdvancedMarketRegimeSurface();

        // AI-analyst client seam (Null/Http). Ships in every edition because the notification enricher
        // resolves IAiAnalystClient; the Pro AI *panels* are added by the Professional shell on top.
        services.AddAiAnalyst(configuration);

        return services;
    }

    /// <summary>Strategy plug-ins: RSI, Cumulative Delta, plus the signal-mode wrappers
    /// around every entry in the backtest catalog.</summary>
    public static IServiceCollection AddStrategyPlugins(this IServiceCollection services)
    {
        services.AddSingleton<IStrategyFactory, StrategyFactory>();
        services.AddBacktestStrategyCatalog();
        // Runtime strategy authoring: Roslyn compiler + the authoring pane VM. Lets users
        // write a strategy and register it into the catalog with no recompile of the host.
        services.AddSingleton<TradingTerminal.Core.Strategies.Authoring.IStrategyCompiler, TradingTerminal.Infrastructure.Strategies.Authoring.RoslynStrategyCompiler>();
        services.AddSingleton<TradingTerminal.App.Authoring.StrategyAuthoringViewModel>();
        services.AddFastBacktestRunner();

        // Shared signal-strategy infrastructure used by every per-strategy project's VM.
        // Lives here once so the 22 Add<Name>Strategy() extensions stay one-liners.
        services.AddSingleton<ISignalGeneratorRouterFactory, SignalGeneratorRouterFactory>();

        // The Index Regime Graph strategy consumes the Advanced Market Regime engine. The tool
        // surface (AddAdvancedMarketRegimeSurface) also registers it, but TryAdd here keeps the
        // strategy resolvable even if that surface isn't wired.
        services.TryAddSingleton<
            Core.MarketData.AdvancedRegime.IAdvancedRegimeProvider,
            TradingTerminal.Infrastructure.Regime.AdvancedRegime.AdvancedRegimeService>();

        // Bundle of canonical-pipeline deps every live strategy VM needs. Resolved once and
        // injected into each per-strategy VM ctor so adding a new strategy doesn't need to
        // touch DI here. The pipeline pieces themselves (IMarketDataHub/Ingest/Store) are
        // registered by AddMarketDataPipeline; the repository facade is registered by
        // AddInfrastructure. This just ties them into a single resolvable bundle.
        services.AddSingleton(sp => new LiveStrategyHostServices(
            sp.GetRequiredService<Core.MarketData.IMarketDataRepository>(),
            sp.GetRequiredService<Core.MarketData.IMarketDataHub>(),
            sp.GetRequiredService<Core.MarketData.IMarketDataIngest>(),
            sp.GetRequiredService<Core.MarketData.IMarketDataStore>(),
            sp.GetRequiredService<Core.Brokers.IBrokerSelector>(),
            sp.GetRequiredService<TradingTerminal.UI.Logging.InMemoryLogSink>(),
            sp.GetRequiredService<Core.MarketData.IInstrumentRegistry>()));

        // NOTE: every first-party strategy is now compile-registered NOWHERE here — each ships as an
        // EXTERNAL plugin (SigmaIcFlow, IndexRegimeGraph, CumulativeDelta, FilteredOrderFlow,
        // OrderFlowCube, OrderFlowSurfaceSpike, ImbalanceHeatFront, IndexKScoreSurface,
        // OrderFlowPressureMap). App.csproj builds each with ReferenceOutputAssembly=false and stages its
        // DLL + plugin.json into {BaseDirectory}/plugins; PluginLoader (below) discovers and registers
        // them at runtime through the SAME DI seam. IndexRegimeGraph consumes only the host-registered
        // Core IAdvancedRegimeProvider (TryAdd'd above), so it needs no host-internal reference.

        // Strategy plugins — discovered at runtime from the app's plugins/ folder (the first-party ones
        // staged by App.csproj, plus any third-party drop-ins), each loaded in its own collectible
        // context and registered through the SAME DI seam (no host recompile). Missing folder = no-op;
        // a bad plugin is logged and skipped, never blocking startup. The open-core marketplace entry point.
        var pluginsRoot = System.IO.Path.Combine(System.AppContext.BaseDirectory, "plugins");
        // Dev/open-core build loads unsigned local plugins. A curated distribution would build this
        // policy from config (pinned publisher thumbprints) instead.
        var pluginPolicy = PluginTrustPolicy.Permissive;
        var loadedPlugins = PluginLoader.LoadInto(
            services, pluginsRoot, DaxAlgo.Sdk.SdkInfo.Version,
            onError: (path, ex) => Serilog.Log.Warning(ex, "Failed to load strategy plugin {Path}", path));
        foreach (var plugin in loadedPlugins)
            Serilog.Log.Information("Loaded strategy plugin {Name} (DaxAlgo.Sdk {Sdk})", plugin.Name, plugin.TargetSdkVersion);

        // Surface the plugin subsystem to the Plugin Manager UI (what's loaded, where, under which policy).
        services.AddSingleton(new PluginHostContext(pluginsRoot, pluginPolicy, loadedPlugins));

        // Plugin Manager tool window (View → "Manage strategy plugins…").
        services.AddTransient<TradingTerminal.App.Plugins.PluginManagerViewModel>();
        services.AddTransient<TradingTerminal.App.Plugins.PluginManagerView>();

        return services;
    }

    /// <summary>The main-shell window and its view-model, plus the factory seam over the login +
    /// main windows so <c>App.xaml.cs</c> never references the concrete window types. The login
    /// window / forms / credential store are registered by <c>AddLogin()</c> in the
    /// TradingTerminal.Login project.</summary>
    public static IServiceCollection AddShell(this IServiceCollection services)
    {
        // Generic single-instance window machinery shared by the shell VM and the per-edition
        // tier-exclusive launchers (IShellExtendedToolCommands).
        services.AddSingleton<IShellWindowHost, ShellWindowHost>();

        // Broker-API meter VM — singleton because it owns a DispatcherTimer and a stable
        // ObservableCollection of chips bound by the header strip. Resolves IBrokerApiMeter
        // from Infrastructure (registered alongside the broker clients).
        services.AddSingleton<TradingTerminal.App.BrokerMetering.BrokerApiMeterViewModel>();

        // MainWindowViewModel is Singleton because there's one main shell at a time and
        // it holds the docked tab collection / active strategy list. It resolves
        // BacktestViewModel / NotificationsSettingsViewModel transiently on each open,
        // so opening a tab twice gets a fresh VM — service-locator pattern is intentional
        // for lazy tab construction.
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<MainWindow>();

        services.AddSingleton<ILoginShellFactory, LoginShellFactory>();
        services.AddSingleton<IMainShellFactory, MainShellFactory>();
        return services;
    }

    /// <summary>The "Support the developer" surface: the thank-you / feedback window plus the prompt
    /// service that shows it once per launch and backs the Help menu. No secrets — feedback is
    /// delivered via a <c>mailto:</c> the user's own mail client sends.</summary>
    public static IServiceCollection AddSupport(this IServiceCollection services)
    {
        services.AddTransient<TradingTerminal.App.Support.SupportViewModel>();
        services.AddTransient<TradingTerminal.App.Support.SupportWindow>();
        services.AddSingleton<TradingTerminal.App.Support.ISupportPrompt, TradingTerminal.App.Support.SupportPrompt>();
        return services;
    }

    /// <summary>Settings dialogs (notifications + the Theme Studio). Add new settings tabs here.</summary>
    public static IServiceCollection AddSettingsSurface(this IServiceCollection services)
    {
        services.AddTransient<NotificationsSettingsViewModel>();
        services.AddTransient<NotificationsSettingsView>();
        services.AddTransient<TradingTerminal.App.Theming.ThemeStudioViewModel>();
        services.AddTransient<TradingTerminal.App.Theming.ThemeStudioView>();
        services.AddTransient<TradingTerminal.App.Research.ResearchSettingsViewModel>();
        services.AddTransient<TradingTerminal.App.Research.ResearchSettingsView>();
        return services;
    }

    /// <summary>Market-data archive UI: settings + activity tabs, plus the WPF bridge that
    /// fulfils Telegram's verification-code / 2FA prompts via a modal dialog. Replaces the
    /// NullTelegramAuthPrompt registered by AddMarketDataArchive so the login flow can interact
    /// with the user.</summary>
    public static IServiceCollection AddArchiveSurface(this IServiceCollection services)
    {
        services.AddSingleton<TradingTerminal.Infrastructure.MarketData.Archive.Telegram.ITelegramAuthPrompt,
            WpfTelegramAuthPrompt>();
        // Login-window seam for the Telegram archive credentials — implemented here (App owns the
        // persistence + transport + auth prompt) so the Login project stays Core+UI only.
        services.AddSingleton<TradingTerminal.Core.MarketData.Archive.ITelegramArchiveLogin,
            TelegramArchiveLogin>();
        services.AddTransient<ArchiveSettingsViewModel>();
        services.AddTransient<ArchiveSettingsView>();
        services.AddTransient<ArchiveActivityViewModel>();
        services.AddTransient<ArchiveActivityView>();

        // Decrypts the DPAPI-encrypted *EncryptedBase64 fields back into ApiHash / PhoneNumber on
        // every IOptionsMonitor read. Legacy plaintext fields written by older builds remain
        // intact and get migrated to encrypted form on next save via ArchiveUserFile.
        services.AddSingleton<
            Microsoft.Extensions.Options.IPostConfigureOptions<TradingTerminal.Core.Configuration.TelegramArchiveOptions>,
            TelegramArchiveOptionsPostConfigure>();
        return services;
    }
}
