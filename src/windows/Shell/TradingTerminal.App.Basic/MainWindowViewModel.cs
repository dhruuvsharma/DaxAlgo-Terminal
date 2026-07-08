using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.App.Archive;
using TradingTerminal.App.BrokerMetering;
using TradingTerminal.App.Notifications;
using TradingTerminal.App.Shell;
// Common per-tool projects: Charts menu, Tools menu (every edition ships these).
using TradingTerminal.Charts;
using TradingTerminal.OrderBook;
using TradingTerminal.VolumeFootprint;
using TradingTerminal.Correlation;
using TradingTerminal.Heatmap;
using TradingTerminal.Backtest;
using TradingTerminal.BacktestStudio;
using TradingTerminal.Recording;
using TradingTerminal.AdvancedMarketRegime;
using TradingTerminal.Infrastructure.MarketData.Store;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Events;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Session;
using TradingTerminal.Core.Strategies;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;
using TradingTerminal.UI.Theming;

namespace TradingTerminal.App;

public sealed partial class MainWindowViewModel : ViewModelBase, IShellOverlayPresenter
{
    // Stable per-window keys for the single-instance window registry (owned by IShellWindowHost).
    private const string NotificationsWindowId = "settings.notifications";
    private const string PluginManagerWindowId = "plugins.manager";
    private const string BacktestStudioWindowId = "tools.backtest-studio";
    private const string RecorderWindowId = "tools.recorder";
    private const string AdvancedRegimeWindowId = "tools.regime.advanced";
    private const string CorrelationWindowId = "tools.correlation";
    private const string LiveCorrelationWindowId = "tools.correlation.live";
    private const string ChartsWindowId = "tools.charts";
    private const string OrderBookWindowId = "tools.orderbook";
    private const string FootprintWindowId = "tools.footprint";
    private const string BookmapWindowId = "tools.heatmap.bookmap";
    private const string ArchiveSettingsWindowId = "settings.archive";
    private const string ArchiveActivityWindowId = "settings.archive.activity";
    private const string ThemeStudioWindowId = "settings.themestudio";

    private readonly IStrategyFactory _factory;
    private readonly IEventBus _eventBus;
    private readonly SessionContext _session;
    private readonly IBrokerSelector _brokerSelector;
    private readonly IServiceProvider _services;
    private readonly IShellWindowHost _host;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly DispatcherTimer _clockTimer;
    private readonly IThemeManager _themeManager;

    public MainWindowViewModel(
        IStrategyFactory factory,
        IEventBus eventBus,
        InMemoryLogSink logSink,
        SessionContext session,
        IBrokerSelector brokerSelector,
        BrokerApiMeterViewModel apiMeter,
        IShellWindowHost host,
        IServiceProvider services,
        ILogger<MainWindowViewModel> logger)
    {
        _factory = factory;
        _eventBus = eventBus;
        _session = session;
        _brokerSelector = brokerSelector;
        ApiMeter = apiMeter;
        _host = host;
        _services = services;
        _logger = logger;

        // Drive the shell "Opening…" curtain from the window host.
        _host.OverlayPresenter = this;

        Strategies = new ObservableCollection<ITradingStrategy>(factory.All);
        LogSink = logSink;
        ActivityLog = CollectionViewSource.GetDefaultView(logSink.Entries);
        ActivityLog.Filter = FilterActivityEntry;

        _themeManager = services.GetRequiredService<IThemeManager>();
        Themes = new ObservableCollection<ThemeMenuOption>(
            _themeManager.Themes.Select(t => new ThemeMenuOption(t.Id, t.Name)));
        SyncThemeChecks();
        // The Theme Studio can save/import custom themes — rebuild the menu when the set changes.
        _themeManager.ThemesChanged += (_, _) =>
        {
            if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())
                d.BeginInvoke(new Action(RefreshThemeMenu));
            else
                RefreshThemeMenu();
        };

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClocks();
        _clockTimer.Start();
        UpdateClocks();

        // Aggregate connection state across every available broker — when any broker is
        // Connected we report Connected; otherwise mirror the most "alive" state.
        RefreshAggregateState();
        _brokerSelector.StateChanged += (_, _) =>
        {
            if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())
                d.BeginInvoke(new Action(RefreshAggregateState));
            else
                RefreshAggregateState();
        };

        _session.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(SessionUserDisplay));
            OnPropertyChanged(nameof(IsAuthenticated));
        };
    }

    // ── IShellOverlayPresenter — lets the window host drive the shell busy-overlay ──────────────
    void IShellOverlayPresenter.Show(string title, string detail)
    {
        OpeningTitle = title;
        OpeningDetail = detail;
        IsOpening = true;
    }

    void IShellOverlayPresenter.Hide() => IsOpening = false;

    private bool _simWarningLogged;

    private void RefreshAggregateState()
    {
        var available = _brokerSelector.AvailableKinds;
        var states = available.Select(k => _brokerSelector.CurrentStateOf(k)).ToList();

        // Aggregate: any Connected → Connected; else any Connecting/Reconnecting → that state; else Failed/Disconnected.
        if (states.Any(s => s == Core.Domain.ConnectionState.Connected))
            ConnectionState = Core.Domain.ConnectionState.Connected;
        else if (states.Any(s => s == Core.Domain.ConnectionState.Reconnecting))
            ConnectionState = Core.Domain.ConnectionState.Reconnecting;
        else if (states.Any(s => s == Core.Domain.ConnectionState.Connecting))
            ConnectionState = Core.Domain.ConnectionState.Connecting;
        else if (states.Any(s => s == Core.Domain.ConnectionState.Failed))
            ConnectionState = Core.Domain.ConnectionState.Failed;
        else
            ConnectionState = Core.Domain.ConnectionState.Disconnected;

        OnPropertyChanged(nameof(ActiveBrokerLabel));
        OnPropertyChanged(nameof(DisconnectBannerText));
        OnPropertyChanged(nameof(ModeDisplayName));
        OnPropertyChanged(nameof(IsLiveMode));
        OnPropertyChanged(nameof(ConnectedBrokerCount));

        RefreshSimulatedState();
    }

    /// <summary>Surfaces a persistent amber "SIMULATED DATA" banner while the Simulated broker is among
    /// the connected set, and appends a one-time warning to the universal Activity Log so it's on the
    /// record that the feed is synthetic, not live.</summary>
    private void RefreshSimulatedState()
    {
        var simActive = _brokerSelector.Connected.Contains(BrokerKind.Simulated);
        IsSimulatedActive = simActive;
        // Push the same flag to the app-wide signal so every tool/chart/strategy window's banner
        // tracks it (they don't take a broker-selector dependency of their own).
        TradingTerminal.UI.SimulatedDataState.Set(simActive);
        if (simActive && !_simWarningLogged)
        {
            _simWarningLogged = true;
            LogSink.Append("System", "Warning",
                "SIMULATED DATA — the Simulated broker is connected. This is not a live market feed.");
        }
    }

    /// <summary>Refresh the local + UTC clocks and the (approximate, no DST/holiday calendar) market
    /// session flags driven by the 1-second timer.</summary>
    private void UpdateClocks()
    {
        CurrentTime = DateTime.Now.ToString("HH:mm:ss");
        var utc = DateTime.UtcNow;
        CurrentTimeUtc = utc.ToString("HH:mm:ss");
        NyseOpen = IsSessionOpen(utc, 14, 30, 21, 0);  // ~09:30–16:00 ET
        LseOpen = IsSessionOpen(utc, 8, 0, 16, 30);    // ~08:00–16:30 London
    }

    private static bool IsSessionOpen(DateTime utc, int startH, int startM, int endH, int endM)
    {
        if (utc.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var minutes = utc.Hour * 60 + utc.Minute;
        return minutes >= startH * 60 + startM && minutes < endH * 60 + endM;
    }

    public ObservableCollection<ITradingStrategy> Strategies { get; }
    public InMemoryLogSink LogSink { get; }

    /// <summary>Filtered view over the universal activity log shown in the bottom log drawer —
    /// aggregates system (Serilog) and per-strategy/window entries. Filtered live by
    /// <see cref="LogFilter"/> across source / level / message.</summary>
    public ICollectionView ActivityLog { get; }

    /// <summary>Free-text filter over the activity log (matches source, level, or message).</summary>
    [ObservableProperty] private string _logFilter = string.Empty;

    partial void OnLogFilterChanged(string value) => ActivityLog.Refresh();

    private bool FilterActivityEntry(object obj)
    {
        if (obj is not LogEntry e) return false;
        var f = LogFilter?.Trim();
        if (string.IsNullOrEmpty(f)) return true;
        return e.Source.Contains(f, StringComparison.OrdinalIgnoreCase)
            || e.Level.Contains(f, StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Live API-call meter shown as broker chips in the header strip.</summary>
    public BrokerApiMeterViewModel ApiMeter { get; }

    /// <summary>Composite mode label — "Live · IB + cTrader" when multiple brokers are up. Falls
    /// back to the first connected broker's mode for single-broker sessions.</summary>
    public string ModeDisplayName
    {
        get
        {
            var connected = _brokerSelector.Connected;
            return connected.Count switch
            {
                0 => "Disconnected",
                1 => _brokerSelector.ModeOf(connected[0]).DisplayName,
                _ => $"Multi-broker · {string.Join(" + ", connected.Select(Label))}",
            };
        }
    }

    /// <summary>True if ANY connected broker is in live mode.</summary>
    public bool IsLiveMode => _brokerSelector.Connected.Any(k => _brokerSelector.ModeOf(k).IsLive);

    public string ActiveBrokerLabel
    {
        get
        {
            var connected = _brokerSelector.Connected;
            return connected.Count switch
            {
                0 => "(no brokers connected)",
                1 => Label(connected[0]),
                _ => string.Join(" + ", connected.Select(Label)),
            };
        }
    }

    public string DisconnectBannerText => "Disconnected — connect a broker to resume";

    private static string Label(BrokerKind kind) => kind switch
    {
        BrokerKind.InteractiveBrokers => "Interactive Brokers",
        BrokerKind.NinjaTrader => "NinjaTrader",
        BrokerKind.CTrader => "cTrader",
        BrokerKind.Alpaca => "Alpaca",
        _ => kind.ToString(),
    };

    public bool IsAuthenticated => _session.IsAuthenticated;

    public string SessionUserDisplay
    {
        get
        {
            if (!_session.IsAuthenticated) return "Not signed in";
            var user = string.IsNullOrEmpty(_session.Username) ? "Anonymous" : _session.Username;
            return $"{user} · {_session.AccountType}";
        }
    }

    [ObservableProperty]
    private ITradingStrategy? _selectedStrategy;

    [ObservableProperty]
    private ConnectionState _connectionState = Core.Domain.ConnectionState.Disconnected;

    [ObservableProperty]
    private string _currentTime = DateTime.Now.ToString("HH:mm:ss");

    [ObservableProperty]
    private string _currentTimeUtc = DateTime.UtcNow.ToString("HH:mm:ss");

    [ObservableProperty]
    private bool _nyseOpen;

    [ObservableProperty]
    private bool _lseOpen;

    /// <summary>True while the Simulated broker is connected — drives the persistent amber
    /// "SIMULATED DATA" banner in the shell chrome.</summary>
    [ObservableProperty]
    private bool _isSimulatedActive;

    // ── "Opening…" loading curtain ───────────────────────────────────────────────────────────
    // Building a tool/strategy view (ScottPlot, WebView2, Helix, history fetch) is synchronous and
    // briefly freezes the UI. The window host paints a full-window BusyOverlay first (via
    // IShellOverlayPresenter), then defers the heavy build to a Background dispatch so the curtain is
    // on screen before the freeze — the user sees *what* is loading instead of an unresponsive shell.

    /// <summary>True while a window is being constructed — drives the shell's <c>BusyOverlay</c>.</summary>
    [ObservableProperty]
    private bool _isOpening;

    /// <summary>Headline on the opening curtain, e.g. "Opening Order Flow Cube…".</summary>
    [ObservableProperty]
    private string _openingTitle = "Loading…";

    /// <summary>Sub-line on the opening curtain describing what is being prepared.</summary>
    [ObservableProperty]
    private string _openingDetail = string.Empty;

    /// <summary>Count of brokers currently connected — shown in the status bar.</summary>
    public int ConnectedBrokerCount => _brokerSelector.Connected.Count;

    /// <summary>Two-way: the bottom activity-log drawer is open when true. Bound to the drawer
    /// toggle strip and the View → Logs menu item. Closed by default.</summary>
    [ObservableProperty]
    private bool _isLogVisible;

    public bool IsDisconnected => ConnectionState is not Core.Domain.ConnectionState.Connected;

    partial void OnConnectionStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(IsDisconnected));
        _eventBus.Publish(new ConnectionStateChangedEvent(value));
    }

    [RelayCommand]
    public void OpenStrategy(string? strategyId)
    {
        if (string.IsNullOrWhiteSpace(strategyId))
        {
            if (SelectedStrategy is null) return;
            strategyId = SelectedStrategy.Id;
        }

        if (_host.TryActivate(strategyId)) return;

        var stratName = Strategies.FirstOrDefault(s => s.Id == strategyId)?.DisplayName ?? "strategy";
        _host.OpenWithOverlay($"Opening {stratName}…", "Building the live window and warming the data feed…", () =>
        {
            var host = _factory.Create(strategyId);
            var capturedId = strategyId!;

            // Most strategies ship their own MetroWindow (StrategyWindowBase); the rest expose a
            // UserControl view, which we wrap in a generic tool host window.
            var window = host.View as Window ?? ToolHostWindow.Create(host.DisplayName, (FrameworkElement)host.View);
            // Top the amber "SIMULATED DATA" strip on the strategy window too (covers both the
            // StrategyWindowBase windows and the plain-MetroWindow ones; idempotent, so the
            // ToolHostWindow fallback above — already wrapped — is left untouched).
            TradingTerminal.UI.Controls.SimulatedDataBanner.AttachTo(window);
            window.Owner = Application.Current.MainWindow;
            // Open full-size and remember the user's last size/position/state, keyed by strategy id.
            // Centralized here so every strategy window benefits regardless of its base class (the
            // StrategyWindowBase ones and the plain-MetroWindow cube/surface/regime ones alike).
            TradingTerminal.UI.StrategyWindowPlacementStore.Attach(window, capturedId);
            window.Closed += (_, _) =>
            {
                _host.Unregister(capturedId);
                if (host.ViewModel is IDisposable d) d.Dispose();
            };
            _host.Register(capturedId, window);
            window.Show();
            _eventBus.Publish(new StrategyOpenedEvent(host.StrategyId, host.DisplayName));
            _logger.LogInformation("Opened strategy window {Id} ({Name})", host.StrategyId, host.DisplayName);
        });
    }

    /// <summary>
    /// Strategy-catalog "Quick backtest": opens a single-instance-per-strategy results window that
    /// auto-runs a 1-year backtest of the chosen strategy over historical bars (real broker when one
    /// is connected, Simulated synthetic otherwise) and shows P&amp;L + headline statistics. Strategies
    /// that declare no engine-side counterpart (<see cref="ITradingStrategy.BacktestStrategyId"/> is
    /// null) get a message instead of a run.
    /// </summary>
    [RelayCommand]
    public void QuickBacktest(string? strategyId)
    {
        if (string.IsNullOrWhiteSpace(strategyId))
        {
            if (SelectedStrategy is null) return;
            strategyId = SelectedStrategy.Id;
        }

        var strategy = Strategies.FirstOrDefault(s => s.Id == strategyId);
        if (strategy is null) return;

        var windowId = "quickbacktest." + strategyId;
        if (_host.TryActivate(windowId)) return;

        _host.OpenWithOverlay($"Quick backtest — {strategy.DisplayName}…", "Fetching history and replaying through the engine…", () =>
        {
            var vm = _services.GetRequiredService<QuickBacktestViewModel>();
            var view = _services.GetRequiredService<QuickBacktestView>();
            view.DataContext = vm;

            var window = ToolHostWindow.Create($"Quick backtest — {strategy.DisplayName}", view);
            window.Owner = Application.Current.MainWindow;
            window.Closed += (_, _) =>
            {
                _host.Unregister(windowId);
                if (vm is IDisposable d) d.Dispose();
            };
            _host.Register(windowId, window);
            window.Show();

            // Bind the VM to the strategy and kick off the first run after the window is up.
            // Tape-primary strategies (SigmaIcFlow) default to Binance + real-tape mode for a full backtest.
            var preferFullTape = strategy.DataRequirement.HasFlag(Core.Strategies.StrategyDataRequirement.TradeTape);
            vm.Initialize(strategy.BacktestStrategyId, strategy.DisplayName, preferFullTape);
            _logger.LogInformation("Opened quick backtest for {Id} ({Name})", strategy.Id, strategy.DisplayName);
        });
    }

    [RelayCommand]
    public async Task ReconnectAsync()
    {
        _logger.LogInformation("Reconnect requested by user — restarting every available broker");
        foreach (var kind in _brokerSelector.AvailableKinds)
        {
            try { await _brokerSelector.ConnectAsync(kind); }
            catch (Exception ex) { _logger.LogWarning(ex, "Reconnect failed for {Broker}", kind); }
        }
    }

    [RelayCommand]
    public void Exit()
    {
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>File → Start QuestDB. Brings up the QuestDB Docker container (launching Docker Desktop
    /// first if the daemon is down) and re-arms the store so tick persistence engages without a restart.
    /// Progress shows in the activity log. No-op-with-a-message when QuestDB isn't the configured backend.</summary>
    [RelayCommand]
    public async Task StartQuestDbAsync()
    {
        var service = _services.GetRequiredService<QuestDbDockerService>();
        await service.StartAsync().ConfigureAwait(false);
    }

    // ── Theme switching (View → Theme) ────────────────────────────────────────────────────────────

    /// <summary>Selectable app themes, bound to the View → Theme menu. <see cref="ThemeMenuOption.IsCurrent"/>
    /// drives the radio check; the future anime theme is just one more registry entry in ThemeManager.</summary>
    public ObservableCollection<ThemeMenuOption> Themes { get; }

    [RelayCommand]
    private void ApplyTheme(string? themeId)
    {
        if (string.IsNullOrEmpty(themeId)) return;
        _themeManager.Apply(themeId);
        SyncThemeChecks();
    }

    private void SyncThemeChecks()
    {
        foreach (var option in Themes)
            option.IsCurrent = string.Equals(option.Id, _themeManager.CurrentThemeId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rebuilds the View → Theme menu from the manager's registry (after a custom theme is
    /// saved or imported in the Theme Studio).</summary>
    private void RefreshThemeMenu()
    {
        Themes.Clear();
        foreach (var t in _themeManager.Themes)
            Themes.Add(new ThemeMenuOption(t.Id, t.Name));
        SyncThemeChecks();
    }

    [RelayCommand]
    public void OpenThemeStudio() =>
        _host.OpenHostedTool<TradingTerminal.App.Theming.ThemeStudioViewModel, TradingTerminal.App.Theming.ThemeStudioView>(
            ThemeStudioWindowId, "Theme Studio", "Loading the theme editor…");

    /// <summary>Copies the selected activity-log rows to the clipboard (tab-aligned text). Falls back
    /// to copying every currently-visible row when nothing is selected, so Ctrl+C / "Copy" always
    /// yields something useful.</summary>
    [RelayCommand]
    private void CopyLog(System.Collections.IList? selected)
    {
        var rows = selected is { Count: > 0 }
            ? selected.Cast<LogEntry>()
            : ActivityLog.Cast<LogEntry>();
        CopyEntriesToClipboard(rows);
    }

    /// <summary>Copies every row currently visible in the activity log (honouring the active filter).</summary>
    [RelayCommand]
    private void CopyAllLogs() => CopyEntriesToClipboard(ActivityLog.Cast<LogEntry>());

    private static void CopyEntriesToClipboard(IEnumerable<LogEntry> entries)
    {
        var text = string.Join(Environment.NewLine, entries.Select(FormatLogEntry));
        if (string.IsNullOrEmpty(text)) return;
        try { System.Windows.Clipboard.SetText(text); }
        catch { /* clipboard can be transiently locked by another process — ignore */ }
    }

    private static string FormatLogEntry(LogEntry e) =>
        $"{e.TimestampUtc:HH:mm:ss}  {e.Source,-20}  {e.Level,-5}  {e.Message}";

    [RelayCommand]
    public void OpenPluginManager() =>
        _host.OpenHostedTool<TradingTerminal.App.Plugins.PluginManagerViewModel, TradingTerminal.App.Plugins.PluginManagerView>(
            PluginManagerWindowId, "Strategy plugins", "Loading the plugin manager…");

    [RelayCommand]
    public void OpenBacktestStudio() =>
        _host.OpenHostedTool<BacktestStudioViewModel, BacktestStudioView>(BacktestStudioWindowId, "Backtest Studio", "Loading the backtest studio…");

    [RelayCommand]
    public void OpenRecorder() =>
        _host.OpenHostedTool<TickRecorderViewModel, TickRecorderView>(RecorderWindowId, "Record ticks", "Preparing the tick recorder…");

    [RelayCommand]
    public void OpenCorrelation() =>
        _host.OpenWindowTool<CorrelationMatrixViewModel, CorrelationMatrixWindow>(
            CorrelationWindowId, "Correlation matrix", "Computing the correlation matrix…");

    [RelayCommand]
    public void OpenLiveCorrelation() =>
        _host.OpenWindowTool<LiveCorrelationMatrixViewModel, LiveCorrelationMatrixWindow>(
            LiveCorrelationWindowId, "Live correlation matrix", "Wiring the live correlation feed…");

    /// <summary>Help → Support the developer. Routes through the shared prompt service so the window
    /// is single-instance whether opened here or auto-shown on launch.</summary>
    [RelayCommand]
    public void OpenSupport() =>
        _services.GetRequiredService<TradingTerminal.App.Support.ISupportPrompt>()
            .Show(Application.Current.MainWindow);

    [RelayCommand]
    public void OpenCharts() =>
        _host.OpenWindowTool<ChartsViewModel, ChartsWindow>(ChartsWindowId, "Charts", "Starting the charting engine…");

    [RelayCommand]
    public void OpenOrderBook() =>
        _host.OpenWindowTool<OrderBookViewModel, OrderBookWindow>(OrderBookWindowId, "Order book", "Wiring the depth-of-book feed…");

    [RelayCommand]
    public void OpenFootprint() =>
        _host.OpenWindowTool<VolumeFootprintViewModel, VolumeFootprintWindow>(FootprintWindowId, "Volume footprint", "Preparing the volume-footprint grid…");

    [RelayCommand]
    public void OpenBookmap() =>
        _host.OpenWindowTool<BookmapHeatmapViewModel, BookmapHeatmapWindow>(BookmapWindowId, "Bookmap + VolBook", "Building the liquidity heatmap…");

    [RelayCommand]
    public void OpenAdvancedRegime() =>
        // The VM runs an auto-refresh loop; it is IDisposable and stopped when the window closes.
        _host.OpenHostedTool<AdvancedMarketRegimeViewModel, AdvancedMarketRegimeView>(
            AdvancedRegimeWindowId, "Advanced market regime", "Running the regime indicator stack…");

    [RelayCommand]
    public void OpenNotificationsSettings() =>
        _host.OpenHostedTool<NotificationsSettingsViewModel, NotificationsSettingsView>(NotificationsWindowId, "Notifications", "Loading settings…");

    [RelayCommand]
    public void OpenArchiveSettings() =>
        _host.OpenHostedTool<ArchiveSettingsViewModel, ArchiveSettingsView>(ArchiveSettingsWindowId, "Archive settings", "Loading settings…");

    [RelayCommand]
    public void OpenArchiveActivity() => OpenOrActivateArchiveHistory();

    private ArchiveActivityViewModel? _archiveActivityVm;

    private ArchiveActivityViewModel OpenOrActivateArchiveHistory()
    {
        if (_host.IsOpen(ArchiveActivityWindowId) && _archiveActivityVm is not null)
        {
            _host.TryActivate(ArchiveActivityWindowId);
            return _archiveActivityVm;
        }

        var vm = _services.GetRequiredService<ArchiveActivityViewModel>();
        var view = _services.GetRequiredService<ArchiveActivityView>();
        view.DataContext = vm;

        var window = ToolHostWindow.Create("Archive history", view);
        window.Owner = Application.Current.MainWindow;
        window.Closed += (_, _) =>
        {
            _host.Unregister(ArchiveActivityWindowId);
            _archiveActivityVm = null;
        };
        _host.Register(ArchiveActivityWindowId, window);
        _archiveActivityVm = vm;
        window.Show();
        return vm;
    }

    /// <summary>Data → Instant offload: opens the Archive history window and immediately ships every
    /// pending period to Telegram, so the run's progress is visible as it goes.</summary>
    [RelayCommand]
    public void InstantOffload()
    {
        var vm = OpenOrActivateArchiveHistory();
        if (vm.InstantOffloadCommand.CanExecute(null))
            vm.InstantOffloadCommand.Execute(null);
    }

    public Task StartAsync()
    {
        // Connect lifecycle is owned by the login screen and the BrokerSelector now —
        // each broker the user signed into already has its own reconnect loop running.
        // Nothing to do here on shell load.
        return Task.CompletedTask;
    }
}
