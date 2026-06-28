using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradingTerminal.App.Archive;
using TradingTerminal.App.Notifications;
using TradingTerminal.App.Shell;
using TradingTerminal.Core.Strategies;
using TradingTerminal.UI.Strategies;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Backtest.Fast;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.UI;
using TradingTerminal.Strategies.CumulativeDelta;
using TradingTerminal.Strategies.FilteredOrderFlow;
using TradingTerminal.Strategies.ImbalanceHeatFront;
using TradingTerminal.Strategies.IndexKScoreSurface;
using TradingTerminal.Strategies.IndexRegimeGraph;
using TradingTerminal.Strategies.OrderFlowCube;
using TradingTerminal.Strategies.OrderFlowPressureMap;
using TradingTerminal.Strategies.OrderFlowSurfaceSpike;
using TradingTerminal.Strategies.OrderFlowToxicity;
using TradingTerminal.Strategies.OrnsteinUhlenbeck;
using TradingTerminal.Strategies.VolatilityTargeted;

namespace TradingTerminal.App.Composition;

/// <summary>
/// Per-feature DI extension methods used by <c>App.xaml.cs</c>. Splitting these out turns
/// the composition root into a short, readable manifest — each line is a feature module.
/// Adding a new module: write a new <c>AddXxx</c> method here and call it from
/// <c>App.OnStartup</c>.
/// </summary>
public static class AppDependencyInjection
{
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

        // Dedicated live strategies — each in its own project, opens as a MetroWindow.
        services.AddCumulativeDeltaStrategy();

        // HFT / microstructure
        services.AddOrnsteinUhlenbeckStrategy();

        // Index baselines
        services.AddVolatilityTargetedStrategy();

        // L2 / depth-of-market
        services.AddOrderFlowToxicityStrategy();
        services.AddOrderFlowPressureMapStrategy();
        services.AddOrderFlowCubeStrategy();
        services.AddOrderFlowSurfaceSpikeStrategy();
        services.AddImbalanceHeatFrontStrategy();
        // NOTE: SigmaIcFlow is no longer compile-registered here — it ships as an EXTERNAL plugin
        // (staged into {BaseDirectory}/plugins by App.csproj) and loads via PluginLoader below. This
        // is the live proof that a real strategy + its WPF window load cross-load-context.
        services.AddIndexKScoreSurfaceStrategy();
        services.AddIndexRegimeGraphStrategy();
        services.AddFilteredOrderFlowStrategy();

        // Third-party strategy plugins — discovered at runtime from the app's plugins/ folder, each
        // loaded in its own collectible context and registered through the SAME DI seam as the
        // first-party strategies above (no host recompile). Missing folder = no-op; a bad plugin is
        // logged and skipped, never blocking startup. This is the open-core marketplace entry point.
        var pluginsRoot = System.IO.Path.Combine(System.AppContext.BaseDirectory, "plugins");
        var loadedPlugins = PluginLoader.LoadInto(
            services, pluginsRoot, DaxAlgo.Sdk.SdkInfo.Version,
            onError: (path, ex) => Serilog.Log.Warning(ex, "Failed to load strategy plugin {Path}", path));
        foreach (var plugin in loadedPlugins)
            Serilog.Log.Information("Loaded strategy plugin {Name} (DaxAlgo.Sdk {Sdk})", plugin.Name, plugin.TargetSdkVersion);

        return services;
    }

    /// <summary>The main-shell window and its view-model, plus the factory seam over the login +
    /// main windows so <c>App.xaml.cs</c> never references the concrete window types. The login
    /// window / forms / credential store are registered by <c>AddLogin()</c> in the
    /// TradingTerminal.Login project.</summary>
    public static IServiceCollection AddShell(this IServiceCollection services)
    {
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
