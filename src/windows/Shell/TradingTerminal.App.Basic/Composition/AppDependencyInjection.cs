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
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.MarketData;
using TradingTerminal.Infrastructure.MarketData.Archive;
using TradingTerminal.Infrastructure.MarketData.Archive.Lake;
using TradingTerminal.Infrastructure.Notifications;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Plugins.Feed;
using TradingTerminal.Infrastructure.Updates;
using TradingTerminal.UI;
// Feature-module extensions used by this edition.
using TradingTerminal.Login;
using TradingTerminal.Backtest;
using TradingTerminal.Recording;

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
    /// the canonical market-data pipeline, archive, Parquet lake, notifications,
    /// strategy plug-ins, login, the shell + window host, support/settings, and the cross-cutting
    /// singletons. <c>App.xaml.cs</c> calls this after registering the
    /// edition (<see cref="AppEdition"/>) and the broker layer.
    /// </summary>
    public static IServiceCollection AddCoreShell(this IServiceCollection services, IConfiguration configuration)
    {
        // Cross-cutting singletons. The InMemoryLogSink instance is normally registered by the shell
        // bootstrap first (it must be the same instance the Serilog sink writes to), so TryAdd here is
        // a no-op in that case and a safety net otherwise.
        services.TryAddSingleton<InMemoryLogSink>();
        services.TryAddSingleton<IThemeManager, ThemeManager>();

        // Canonical pipeline + archive + parquet + notifications.
        services.AddMarketDataPipeline(configuration);
        services.AddMarketDataArchive(configuration);
        services.AddParquetLake(configuration);
        services.AddNotifications(configuration);

        // Python-sidecar controller seam. The login screen depends on ISidecarController, but the real
        // sidecar host is a Pro-only surface (AddSidecar). Register a no-op fallback here so
        // Basic composes; Pro's AddSidecar (a plain AddSingleton) registers the real
        // one after this and wins.
        services.TryAddSingleton<TradingTerminal.Core.Hosting.ISidecarController,
            TradingTerminal.Core.Hosting.NullSidecarController>();

        // Application update check. Inert unless BOTH Updates:FeedUrl and Updates:FeedPublicKey are
        // configured, which they are not in the shipped build — see AddUpdates.
        services.AddUpdates(configuration);

        // Feature modules common to every edition.
        services.AddStrategyPlugins(configuration);
        services.AddLogin();
        services.AddShell();
        services.AddSupport();
        services.AddSettingsSurface();
        services.AddArchiveSurface();
        services.AddBacktestSurface();
        services.AddRecordingSurface();

        return services;
    }

    /// <summary>Strategy plug-ins: RSI, Cumulative Delta, plus the signal-mode wrappers
    /// around every entry in the backtest catalog.</summary>
    public static IServiceCollection AddStrategyPlugins(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IStrategyFactory, StrategyFactory>();
        services.AddBacktestStrategyCatalog();
        // Runtime strategy authoring: Roslyn compiler + the authoring pane VM. Lets users
        // write a strategy and register it into the catalog with no recompile of the host.
        services.AddSingleton<TradingTerminal.Core.Strategies.Authoring.IStrategyCompiler, TradingTerminal.Infrastructure.Strategies.Authoring.RoslynStrategyCompiler>();
        // Turns a compiled authored strategy into a catalog entry and persistent plugin so it is
        // available immediately and survives restart.
        services.AddSingleton<TradingTerminal.Infrastructure.Strategies.Authoring.AuthoredStrategyInstaller>();
        services.AddSingleton<TradingTerminal.App.Authoring.StrategyAuthoringViewModel>();
        services.AddTransient<TradingTerminal.App.Authoring.StrategyAuthoringView>();
        // Basic does not register a default live-view composer. Authored strategies without their own
        // view remain backtest-only; plugin strategies that ship a view continue to open normally.
        // AI Strategy Builder backend (codegen providers + build-loop orchestrator + context pack) — the
        // authoring pane's AI panel resolves IAiStrategyBuilder from here. Keyless by default (installed
        // agent CLIs + local Ollama); a shell that registers an IAiKeyResolver over its credential store
        // unlocks the keyed providers.
        services.AddStrategyCodegen(configuration);

        // Shared signal-strategy infrastructure used by every per-strategy project's VM.
        // Lives here once so the 22 Add<Name>Strategy() extensions stay one-liners.
        services.AddSingleton<ISignalGeneratorRouterFactory, SignalGeneratorRouterFactory>();

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

        // First-party strategies ship as external plugins; this shell neither references nor stages
        // their implementations, and no strategy DLL is loaded at all since 2026-08-15.

        // Strategy plugins — discovered at runtime from the app's plugins/ folder (the first-party ones
        // staged by App.csproj, plus any third-party drop-ins), each loaded in its own collectible
        // context and registered through the SAME DI seam (no host recompile). Missing folder = no-op;
        // a bad plugin is logged and skipped, never blocking startup. The open-core marketplace entry point.
        var pluginsRoot = System.IO.Path.Combine(System.AppContext.BaseDirectory, "plugins");
        // Trust policy comes from the Plugins config section: Permissive (dev/open-core — unsigned
        // local plugins load) or Curated (pinned publisher thumbprints + required manifest).
        var pluginOptions = configuration.GetSection(PluginsOptions.SectionName).Get<PluginsOptions>() ?? new PluginsOptions();
        var pluginPolicy = PluginTrustPolicy.From(pluginOptions);
        // Strategy DLLs are no longer loaded (2026-08-15). The terminal runs DAXQ artifacts
        // only, so PluginLoader is never invoked; the host context is registered empty so the
        // Plugin Manager and the marketplace feed still resolve. Restoring DLL loading means
        // bringing back PluginLoader.LoadWithReport here - see the archived strategies repo.
        services.AddSingleton(new PluginHostContext(pluginsRoot, pluginPolicy, []));
        services.AddPluginFeed(pluginOptions);
        services.AddTransient<TradingTerminal.App.Plugins.PluginManagerViewModel>();
        services.AddTransient<TradingTerminal.App.Plugins.PluginManagerView>();
        return services;
        

        return services;
    }

    /// <summary>The main-shell window and its view-model, plus the factory seam over the login +
    /// main windows so <c>App.xaml.cs</c> never references the concrete window types. The login
    /// window / forms / credential store are registered by <c>AddLogin()</c> in the
    /// TradingTerminal.Login project.</summary>
    public static IServiceCollection AddShell(this IServiceCollection services)
    {
        // Generic single-instance window machinery shared by shell launchers.
        services.AddSingleton<IShellWindowHost, ShellWindowHost>();

        // Broker-API meter VM — singleton because it owns a DispatcherTimer and a stable
        // ObservableCollection of chips bound by the header strip. Resolves IBrokerApiMeter
        // from Infrastructure (registered alongside the broker clients).
        services.AddSingleton<TradingTerminal.App.BrokerMetering.BrokerApiMeterViewModel>();

        // MainWindowViewModel is Singleton because there's one main shell at a time and it holds the
        // active strategy list. Settings views are resolved transiently on each open.
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
