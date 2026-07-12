using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradingTerminal.App.Archive;
using TradingTerminal.App.Notifications;
using TradingTerminal.App.Shell;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies;
using TradingTerminal.UI.Strategies;
using TradingTerminal.UI.Logging;
using TradingTerminal.UI.Theming;
using TradingTerminal.Infrastructure.AiAnalyst;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Backtest.Fast;
using TradingTerminal.Infrastructure.Strategies.Authoring;
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
    /// The core composition of THIS edition shell (each edition owns its own copy of this file) —
    /// the canonical market-data pipeline, archive, Parquet lake, notifications, market regime,
    /// strategy plug-ins, login, the shell + window host, support/settings, the AI-analyst seam
    /// (Null client by default; the notification enricher depends on it, so it ships in every
    /// edition), and the core tool/chart surfaces (Backtest / Backtest Studio / Recorder /
    /// Correlation / Charts / Order book / Footprint / Bookmap+Heatmap / Advanced market regime),
    /// plus the cross-cutting singletons. <c>App.xaml.cs</c> calls this after registering the
    /// edition (<see cref="AppEdition"/>) and the broker layer.
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
        services.AddStrategyPlugins(configuration);
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
    public static IServiceCollection AddStrategyPlugins(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IStrategyFactory, StrategyFactory>();
        services.AddBacktestStrategyCatalog();
        // Runtime strategy authoring: Roslyn compiler + the authoring pane VM. Lets users
        // write a strategy and register it into the catalog with no recompile of the host.
        services.AddSingleton<TradingTerminal.Core.Strategies.Authoring.IStrategyCompiler, TradingTerminal.Infrastructure.Strategies.Authoring.RoslynStrategyCompiler>();
        services.AddSingleton<TradingTerminal.App.Authoring.StrategyAuthoringViewModel>();
        services.AddFastBacktestRunner();
        // AI Strategy Builder backend (codegen providers + build-loop orchestrator + context pack) — the
        // authoring pane's AI panel resolves IAiStrategyBuilder from here. Keyless by default (installed
        // agent CLIs + local Ollama); a shell that registers an IAiKeyResolver over its credential store
        // unlocks the keyed providers.
        services.AddStrategyCodegen(configuration);

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
        // Trust policy comes from the Plugins config section: Permissive (dev/open-core — unsigned
        // local plugins load) or Curated (pinned publisher thumbprints + required manifest).
        var pluginOptions = configuration.GetSection(PluginsOptions.SectionName).Get<PluginsOptions>() ?? new PluginsOptions();
        var pluginPolicy = PluginTrustPolicy.From(pluginOptions);
        // Persisted lifecycle state (user disables, fault quarantines, pending uninstalls) — honoured
        // by the loader BEFORE any plugin code runs; mutated by the Plugin Manager.
        var pluginState = new PluginStateStore(pluginsRoot);
        if (pluginState.LoadError is not null)
            Serilog.Log.Warning("Plugin state reset: {Reason}", pluginState.LoadError);
        // A plugin the host can't vouch for (unsigned, not one of ours, no pinned publisher) is not
        // silently loaded and not silently dropped: the user is shown what it is and what its code
        // reaches for, and decides. The decision is remembered against the file's sha256, so an update
        // has to ask again.
        var pluginReport = PluginLoader.LoadWithReport(services, pluginsRoot, DaxAlgo.Sdk.SdkInfo.Version,
            pluginPolicy, pluginState, pluginOptions.ScanMode, new TradingTerminal.App.Plugins.PluginConsentPrompt());
        foreach (var plugin in pluginReport.Loaded)
            // Attribution: every DI registration in the app is traceable to the plugin that made it.
            Serilog.Log.Information("Loaded strategy plugin {Name} (DaxAlgo.Sdk {Sdk}) registering {Services}",
                plugin.Name, plugin.TargetSdkVersion, plugin.RegisteredServices ?? []);
        foreach (var problem in pluginReport.Problems)
            Serilog.Log.Warning("Strategy plugin {Plugin} did not load ({Outcome}): {Reason}",
                problem.PluginFolderName, problem.Outcome, problem.Reason);

        // Surface the plugin subsystem to the Plugin Manager UI + the header problem chip
        // (what loaded, what didn't and why, and the mutable lifecycle state).
        services.AddSingleton(new PluginHostContext(pluginsRoot, pluginPolicy, pluginReport.Loaded, pluginReport, pluginState));

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

    /// <summary>Settings dialogs (notifications + the Theme Studio). Add new settings tabs here.
    /// (No Research settings — the Paper Lab / sidecar surface is Professional-only.)</summary>
    public static IServiceCollection AddSettingsSurface(this IServiceCollection services)
    {
        services.AddTransient<NotificationsSettingsViewModel>();
        services.AddTransient<NotificationsSettingsView>();
        services.AddTransient<TradingTerminal.App.Theming.ThemeStudioViewModel>();
        services.AddTransient<TradingTerminal.App.Theming.ThemeStudioView>();
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
