using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.App.Ai;
using TradingTerminal.App.AiAnalyst;
using TradingTerminal.App.Archive;
using TradingTerminal.App.Backtest;
using TradingTerminal.App.Login;
using TradingTerminal.App.Login.Forms;
using TradingTerminal.App.Notifications;
using TradingTerminal.App.Recording;
using TradingTerminal.App.Regime;
using TradingTerminal.App.Research;
using TradingTerminal.App.Shell;
using TradingTerminal.App.Strategies;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Infrastructure.Backtest;
using TradingTerminal.Infrastructure.Backtest.Fast;
using TradingTerminal.UI;
using TradingTerminal.Strategies.AnomalyDetector;
using TradingTerminal.Strategies.ApexScalper;
using TradingTerminal.Strategies.AvellanedaStoikov;
using TradingTerminal.Strategies.Bollinger;
using TradingTerminal.Strategies.BookPressure;
using TradingTerminal.Strategies.ConnorsRsi2;
using TradingTerminal.Strategies.CumulativeDelta;
using TradingTerminal.Strategies.EodMomentum;
using TradingTerminal.Strategies.GapFade;
using TradingTerminal.Strategies.IcebergDetection;
using TradingTerminal.Strategies.ImbalanceHeatFront;
using TradingTerminal.Strategies.LiquiditySweep;
using TradingTerminal.Strategies.LondonOpenBreakout;
using TradingTerminal.Strategies.MaCrossover;
using TradingTerminal.Strategies.Macd;
using TradingTerminal.Strategies.Microprice;
using TradingTerminal.Strategies.OnlineRegressionAlpha;
using TradingTerminal.Strategies.OrderFlowCube;
using TradingTerminal.Strategies.OrderFlowSurfaceSpike;
using TradingTerminal.Strategies.OrderFlowToxicity;
using TradingTerminal.Strategies.OrnsteinUhlenbeck;
using TradingTerminal.Strategies.PullbackContinuation;
using TradingTerminal.Strategies.Rsi;
using TradingTerminal.Strategies.ThinBookFilter;
using TradingTerminal.Strategies.TrendFilter;
using TradingTerminal.Strategies.Twap;
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
            sp.GetRequiredService<Core.Brokers.IBrokerSelector>()));

        // Dedicated live strategies — each in its own project, opens as a MetroWindow.
        services.AddRsiStrategy();
        services.AddCumulativeDeltaStrategy();

        // HFT / microstructure
        services.AddMicropriceStrategy();
        services.AddOrnsteinUhlenbeckStrategy();
        services.AddAvellanedaStoikovStrategy();
        services.AddTwapStrategy();

        // Forex baselines
        services.AddBollingerStrategy();
        services.AddMaCrossoverStrategy();
        services.AddConnorsRsi2Strategy();
        services.AddLondonOpenBreakoutStrategy();
        services.AddMacdStrategy();

        // Index baselines
        services.AddTrendFilterStrategy();
        services.AddVolatilityTargetedStrategy();
        services.AddGapFadeStrategy();
        services.AddEodMomentumStrategy();
        services.AddPullbackContinuationStrategy();

        // L2 / depth-of-market
        services.AddBookPressureStrategy();
        services.AddLiquiditySweepStrategy();
        services.AddIcebergDetectionStrategy();
        services.AddOrderFlowToxicityStrategy();
        services.AddOrderFlowCubeStrategy();
        services.AddOrderFlowSurfaceSpikeStrategy();
        services.AddImbalanceHeatFrontStrategy();
        services.AddThinBookFilterStrategy();
        services.AddApexScalperStrategy();

        // ML / AI
        services.AddOnlineRegressionAlphaStrategy();
        services.AddAnomalyDetectorStrategy();

        return services;
    }

    /// <summary>Per-broker login forms. Each form is registered as both its concrete type
    /// (for the factory's GetRequiredService lookup) and as <see cref="IBrokerLoginForm"/>
    /// (so the factory can enumerate them).</summary>
    public static IServiceCollection AddBrokerLoginForms(this IServiceCollection services)
    {
        services.AddSingleton<IbLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<IbLoginFormViewModel>());

        services.AddSingleton<NinjaLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<NinjaLoginFormViewModel>());

        services.AddSingleton<CTraderLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<CTraderLoginFormViewModel>());

        services.AddSingleton<AlpacaLoginFormViewModel>();
        services.AddSingleton<IBrokerLoginForm>(sp => sp.GetRequiredService<AlpacaLoginFormViewModel>());

        services.AddSingleton<IBrokerLoginFormFactory, BrokerLoginFormFactory>();
        return services;
    }

    /// <summary>The login + main-shell windows and their view-models, plus the factory
    /// seam over them so <c>App.xaml.cs</c> never references the concrete window types.</summary>
    public static IServiceCollection AddShell(this IServiceCollection services)
    {
        services.AddSingleton<CredentialStore>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<LoginWindow>();

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

    /// <summary>Backtest tab — view + view-model resolved lazily when the user opens it
    /// from Tools → Backtest. <see cref="IBacktestSession"/> is the engine seam so the VM
    /// stays testable; transient lifetime so each open of the tab gets a fresh session
    /// object (the session itself is stateless across runs, but the lifetime aligns with
    /// the VM's).</summary>
    public static IServiceCollection AddBacktestSurface(this IServiceCollection services)
    {
        services.AddTransient<IBacktestSession, BacktestSession>();
        services.AddTransient<BacktestViewModel>();
        services.AddTransient<BacktestView>();
        return services;
    }

    /// <summary>Settings dialogs (today: notifications). Add new settings tabs here.</summary>
    public static IServiceCollection AddSettingsSurface(this IServiceCollection services)
    {
        services.AddTransient<NotificationsSettingsViewModel>();
        services.AddTransient<NotificationsSettingsView>();
        return services;
    }

    /// <summary>Live tick recorder tab — opens from Tools → Record live ticks.</summary>
    public static IServiceCollection AddRecordingSurface(this IServiceCollection services)
    {
        services.AddTransient<TickRecorderViewModel>();
        services.AddTransient<TickRecorderView>();
        return services;
    }

    /// <summary>Factor research notebook tab — opens from AI tools → Factor research.</summary>
    public static IServiceCollection AddResearchSurface(this IServiceCollection services)
    {
        services.AddTransient<FactorResearchViewModel>();
        services.AddTransient<FactorResearchView>();
        return services;
    }

    /// <summary>AI tools tabs — ML features (triple-barrier labelling) and Backtest analysis
    /// (walk-forward + Monte Carlo). Both wrap Core/Infrastructure types shared with the
    /// daxalgo-backtest CLI so numbers match between the two surfaces.</summary>
    public static IServiceCollection AddAiSurface(this IServiceCollection services)
    {
        services.AddTransient<MlFeaturesViewModel>();
        services.AddTransient<MlFeaturesView>();
        services.AddTransient<BacktestAnalysisViewModel>();
        services.AddTransient<BacktestAnalysisView>();
        return services;
    }

    /// <summary>AI Market Analyst tab — opens from AI tools → Market analyst.</summary>
    public static IServiceCollection AddAiAnalystSurface(this IServiceCollection services)
    {
        services.AddTransient<AiAnalystViewModel>();
        services.AddTransient<AiAnalystView>();
        return services;
    }

    /// <summary>Market Regime tab — opens from Tools → Market regime. The provider and refresh
    /// loop live in Infrastructure (registered via AddMarketRegime); only the panel is here.
    /// Transient so each open gets a fresh subscription that disposes with the tab.</summary>
    public static IServiceCollection AddRegimeSurface(this IServiceCollection services)
    {
        services.AddTransient<MarketRegimeViewModel>();
        services.AddTransient<MarketRegimeView>();
        services.AddSingleton<TradingTerminal.Core.Regime.Instrument.IInstrumentRegimeProvider,
                              TradingTerminal.Infrastructure.Regime.Instrument.InstrumentRegimeService>();
        services.AddTransient<TradingTerminal.App.Regime.Instrument.InstrumentRegimeViewModel>();
        services.AddTransient<TradingTerminal.App.Regime.Instrument.InstrumentRegimeView>();
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
