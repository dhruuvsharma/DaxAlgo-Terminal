using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.App.Archive;
using TradingTerminal.App.Notifications;
using TradingTerminal.App.Shell;
using TradingTerminal.App.Strategies;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Backtest.Fast;
using TradingTerminal.UI;
using TradingTerminal.Strategies.ApexScalper;
using TradingTerminal.Strategies.CumulativeDelta;
using TradingTerminal.Strategies.ImbalanceHeatFront;
using TradingTerminal.Strategies.IndexKScoreSurface;
using TradingTerminal.Strategies.OrderFlowCube;
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
        // Lives here once so the 21 Add<Name>Strategy() extensions stay one-liners.
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
            sp.GetRequiredService<TradingTerminal.UI.Logging.InMemoryLogSink>()));

        // Dedicated live strategies — each in its own project, opens as a MetroWindow.
        services.AddCumulativeDeltaStrategy();

        // HFT / microstructure
        services.AddOrnsteinUhlenbeckStrategy();

        // Index baselines
        services.AddVolatilityTargetedStrategy();

        // L2 / depth-of-market
        services.AddOrderFlowToxicityStrategy();
        services.AddOrderFlowCubeStrategy();
        services.AddOrderFlowSurfaceSpikeStrategy();
        services.AddImbalanceHeatFrontStrategy();
        services.AddApexScalperStrategy();
        services.AddIndexKScoreSurfaceStrategy();

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

    /// <summary>Settings dialogs (today: notifications). Add new settings tabs here.</summary>
    public static IServiceCollection AddSettingsSurface(this IServiceCollection services)
    {
        services.AddTransient<NotificationsSettingsViewModel>();
        services.AddTransient<NotificationsSettingsView>();
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
