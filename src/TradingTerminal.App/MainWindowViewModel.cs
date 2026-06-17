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
// Per-tool projects (extracted from App): Charts menu, Tools menu, AI tools.
using TradingTerminal.Charts;
using TradingTerminal.OrderBook;
using TradingTerminal.VolumeFootprint;
using TradingTerminal.Correlation;
using TradingTerminal.Heatmap;
using TradingTerminal.Backtest;
using TradingTerminal.Recording;
using TradingTerminal.MarkovRegime;
using TradingTerminal.AdvancedMarketRegime;
using TradingTerminal.Ml.Stationarity;
using TradingTerminal.Ml.ArimaGarch;
using TradingTerminal.Ml.KalmanFilter;
using TradingTerminal.Ai.MarketAnalyst;
using TradingTerminal.Ai.FactorResearch;
using TradingTerminal.Ai.MlFeatures;
using TradingTerminal.Ai.BacktestAnalysis;
using TradingTerminal.QuantConnect;
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

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private const string NotificationsSettingsTabId = "settings.notifications";
    private const string BacktestTabId = "tools.backtest";
    private const string RecorderTabId = "tools.recorder";
    private const string ResearchTabId = "tools.research";
    private const string MlFeaturesTabId = "ai.mlfeatures";
    private const string BacktestAnalysisTabId = "ai.backtestanalysis";
    private const string AiAnalystTabId = "ai.marketanalyst";
    private const string MarkovRegimeTabId = "tools.regime.markov";
    private const string AdvancedRegimeTabId = "tools.regime.advanced";
    private const string StationarityTabId = "ml.stationarity";
    private const string ArimaGarchTabId = "ml.arimagarch";
    private const string KalmanFilterTabId = "ml.kalmanfilter";
    private const string CorrelationWindowId = "tools.correlation";
    private const string LiveCorrelationWindowId = "tools.correlation.live";
    private const string ChartsWindowId = "tools.charts";
    private const string QuantConnectWindowId = "tools.quantconnect";
    private const string OrderBookWindowId = "tools.orderbook";
    private const string FootprintWindowId = "tools.footprint";
    private const string BookmapWindowId = "tools.heatmap.bookmap";
    private const string ArchiveSettingsTabId = "settings.archive";
    private const string ArchiveActivityTabId = "settings.archive.activity";

    private readonly IStrategyFactory _factory;
    private readonly IEventBus _eventBus;
    private readonly SessionContext _session;
    private readonly IBrokerSelector _brokerSelector;
    private readonly IServiceProvider _services;
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
        IServiceProvider services,
        ILogger<MainWindowViewModel> logger)
    {
        _factory = factory;
        _eventBus = eventBus;
        _session = session;
        _brokerSelector = brokerSelector;
        ApiMeter = apiMeter;
        _services = services;
        _logger = logger;

        Strategies = new ObservableCollection<ITradingStrategy>(factory.All);
        OpenTabs = new ObservableCollection<DockTab>();
        _openWindows = new Dictionary<string, Window>(StringComparer.Ordinal);
        _tabDisposables = new Dictionary<DockTab, IDisposable>();
        LogSink = logSink;
        ActivityLog = CollectionViewSource.GetDefaultView(logSink.Entries);
        ActivityLog.Filter = FilterActivityEntry;

        Ticker = new Shell.TickerTapeViewModel(
            services.GetRequiredService<IMarketDataRepository>(),
            brokerSelector,
            services.GetRequiredService<ILogger<Shell.TickerTapeViewModel>>());

        _themeManager = services.GetRequiredService<IThemeManager>();
        Themes = new ObservableCollection<ThemeMenuOption>(
            _themeManager.Themes.Select(t => new ThemeMenuOption(t.Id, t.Name)));
        SyncThemeChecks();

        OpenTabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoOpenTabs));

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

    /// <summary>Parse the terminal command line and route to the matching window. Accepts bare tool
    /// keywords (with or without a leading '/'); anything else is treated as a symbol request and opens
    /// Charts. Mirrors a Bloomberg-style command entry.</summary>
    [RelayCommand]
    private void RunCommandLine()
    {
        var raw = (CommandText ?? string.Empty).Trim();
        if (raw.Length == 0) return;
        var cmd = raw.TrimStart('/').Trim().ToLowerInvariant();

        switch (cmd)
        {
            case "charts": case "chart": case "c": OpenCharts(); break;
            case "orderbook": case "book": case "dom": OpenOrderBook(); break;
            case "footprint": case "fp": OpenFootprint(); break;
            case "heatmap": case "hm": case "bookmap": case "volbook": OpenBookmap(); break;
            case "backtest": case "bt": OpenBacktest(); break;
            case "regime": case "rg": OpenAdvancedRegime(); break;
            case "correlation": case "corr": OpenCorrelation(); break;
            case "quantconnect": case "lean": case "qc": OpenQuantConnectBacktest(); break;
            case "research": case "factor": OpenResearch(); break;
            case "analyst": case "ai": OpenAiAnalyst(); break;
            case "recorder": case "record": case "rec": OpenRecorder(); break;
            case "stationarity": case "arima": case "kalman": OpenStationarity(); break;
            case "help": case "?": OpenSupport(); break;
            default:
                // Treat an unknown bare token as a symbol → open Charts and note it in the log.
                LogSink.Append("Command", "Information", $"> {raw}  (opening Charts)");
                OpenCharts();
                break;
        }

        CommandText = string.Empty;
    }

    private readonly Dictionary<string, Window> _openWindows;
    private readonly Dictionary<DockTab, IDisposable> _tabDisposables;

    public ObservableCollection<ITradingStrategy> Strategies { get; }
    public ObservableCollection<DockTab> OpenTabs { get; }
    public InMemoryLogSink LogSink { get; }

    /// <summary>Filtered view over the universal activity log shown in the ACTIVITY LOG dock —
    /// aggregates system (Serilog) and per-strategy/tab entries. Filtered live by
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

    /// <summary>Live API-call meter shown as broker chips in the Bloomberg header strip.</summary>
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
    private DockTab? _activeTab;

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

    /// <summary>Bound to the terminal command line in the header. <see cref="RunCommandLineCommand"/>
    /// parses it and routes to the matching tool/window.</summary>
    [ObservableProperty]
    private string _commandText = string.Empty;

    // ── "Opening…" loading curtain ───────────────────────────────────────────────────────────
    // Building a tool/strategy view (ScottPlot, WebView2, Helix, history fetch) is synchronous and
    // briefly freezes the UI. We paint a full-window BusyOverlay first, then defer the heavy build to
    // a Background dispatch so the curtain is on screen before the freeze — the user sees *what* is
    // loading instead of an unresponsive shell.

    /// <summary>True while a window/tab is being constructed — drives the shell's <c>BusyOverlay</c>.</summary>
    [ObservableProperty]
    private bool _isOpening;

    /// <summary>Headline on the opening curtain, e.g. "Opening Order Flow Cube…".</summary>
    [ObservableProperty]
    private string _openingTitle = "Loading…";

    /// <summary>Sub-line on the opening curtain describing what is being prepared.</summary>
    [ObservableProperty]
    private string _openingDetail = string.Empty;

    /// <summary>Live ticker tape feed shown under the menu bar.</summary>
    public Shell.TickerTapeViewModel Ticker { get; }

    /// <summary>Count of brokers currently connected — shown in the status bar.</summary>
    public int ConnectedBrokerCount => _brokerSelector.Connected.Count;

    /// <summary>True when no document tabs are open — drives the empty-state hint over the dock.</summary>
    public bool HasNoOpenTabs => OpenTabs.Count == 0;

    /// <summary>Two-way: View → Strategies menu binds here; the dock pane's IsHidden flips on this.</summary>
    [ObservableProperty]
    private bool _isStrategiesVisible = true;

    /// <summary>Two-way: View → Logs menu binds here; the dock pane's IsHidden flips on this.</summary>
    [ObservableProperty]
    private bool _isLogsVisible = true;

    public bool IsDisconnected => ConnectionState is not Core.Domain.ConnectionState.Connected;

    partial void OnConnectionStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(IsDisconnected));
        _eventBus.Publish(new ConnectionStateChangedEvent(value));
    }

    /// <summary>
    /// Shows the shell loading curtain (<paramref name="title"/>/<paramref name="detail"/>), then runs
    /// <paramref name="build"/> on a Background dispatch so the curtain paints before the synchronous
    /// view construction freezes the UI thread. The curtain is always taken down afterwards, even if
    /// <paramref name="build"/> throws.
    /// </summary>
    private void OpenWithOverlay(string title, string detail, Action build)
    {
        OpeningTitle = title;
        OpeningDetail = detail;
        IsOpening = true;

        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try { build(); }
            catch (Exception ex) { _logger.LogError(ex, "Failed while opening {Title}", title); }
            finally { IsOpening = false; }
        }));
    }

    /// <summary>Opens (or focuses) a dock-tab tool, resolving its VM+view from DI and building it behind
    /// the loading curtain. <paramref name="trackDisposable"/> registers an <see cref="IDisposable"/> VM
    /// for teardown when the tab closes (e.g. a VM running an auto-refresh loop).</summary>
    private void OpenTabTool<TVm, TView>(string tabId, string tabTitle, string detail, bool trackDisposable = false)
        where TVm : class
        where TView : FrameworkElement
    {
        var existing = OpenTabs.FirstOrDefault(t => t.ContentId == tabId);
        if (existing is not null) { ActiveTab = existing; return; }

        OpenWithOverlay($"Opening {tabTitle}…", detail, () =>
        {
            var vm = _services.GetRequiredService<TVm>();
            var view = _services.GetRequiredService<TView>();
            view.DataContext = vm;

            var tab = new DockTab { Title = tabTitle, ContentId = tabId, Content = view, CanClose = true };
            if (trackDisposable && vm is IDisposable d) _tabDisposables[tab] = d;
            OpenTabs.Add(tab);
            ActiveTab = tab;
        });
    }

    /// <summary>Opens (or focuses) a single-instance tool window, resolving its VM+window from DI and
    /// building it behind the loading curtain. The VM is disposed when the window closes.</summary>
    private void OpenWindowTool<TVm, TWindow>(string windowId, string title, string detail)
        where TVm : class
        where TWindow : Window
    {
        if (_openWindows.TryGetValue(windowId, out var existing)) { existing.Activate(); return; }

        OpenWithOverlay($"Opening {title}…", detail, () =>
        {
            var vm = _services.GetRequiredService<TVm>();
            var window = _services.GetRequiredService<TWindow>();
            window.DataContext = vm;
            window.Owner = Application.Current.MainWindow;
            window.Closed += (_, _) =>
            {
                _openWindows.Remove(windowId);
                if (vm is IDisposable d) d.Dispose();
            };
            _openWindows[windowId] = window;
            window.Show();
            _logger.LogInformation("Opened {Title} window", title);
        });
    }

    [RelayCommand]
    public void OpenStrategy(string? strategyId)
    {
        if (string.IsNullOrWhiteSpace(strategyId))
        {
            if (SelectedStrategy is null) return;
            strategyId = SelectedStrategy.Id;
        }

        if (_openWindows.TryGetValue(strategyId, out var existingWindow))
        {
            existingWindow.Activate();
            return;
        }

        var existingTab = OpenTabs.FirstOrDefault(t => t.ContentId == strategyId);
        if (existingTab is not null)
        {
            ActiveTab = existingTab;
            _logger.LogDebug("Focused existing tab {Id}", strategyId);
            return;
        }

        var stratName = Strategies.FirstOrDefault(s => s.Id == strategyId)?.DisplayName ?? "strategy";
        OpenWithOverlay($"Opening {stratName}…", "Building the live window and warming the data feed…", () =>
        {
            var host = _factory.Create(strategyId);

            if (host.View is Window window)
            {
                var capturedId = strategyId;
                window.Owner = Application.Current.MainWindow;
                window.Closed += (_, _) =>
                {
                    _openWindows.Remove(capturedId);
                    if (host.ViewModel is IDisposable d) d.Dispose();
                };
                _openWindows[capturedId] = window;
                window.Show();
                _eventBus.Publish(new StrategyOpenedEvent(host.StrategyId, host.DisplayName));
                _logger.LogInformation("Opened strategy window {Id} ({Name})", host.StrategyId, host.DisplayName);
                return;
            }

            var tab = new DockTab
            {
                Title = host.DisplayName,
                ContentId = host.StrategyId,
                Content = host.View,
                CanClose = true,
            };
            if (host.ViewModel is IDisposable disposable)
                _tabDisposables[tab] = disposable;
            OpenTabs.Add(tab);
            ActiveTab = tab;
            _eventBus.Publish(new StrategyOpenedEvent(host.StrategyId, host.DisplayName));
            _logger.LogInformation("Opened strategy tab {Id} ({Name})", host.StrategyId, host.DisplayName);
        });
    }

    [RelayCommand]
    public void CloseTab(DockTab? tab)
    {
        if (tab is null) return;
        OpenTabs.Remove(tab);
        if (ReferenceEquals(ActiveTab, tab)) ActiveTab = OpenTabs.LastOrDefault();
        if (_tabDisposables.Remove(tab, out var disposable)) disposable.Dispose();
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

    [RelayCommand]
    public void ShowStrategies() => IsStrategiesVisible = true;

    [RelayCommand]
    public void ShowLogs() => IsLogsVisible = true;

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
    public void OpenBacktest() =>
        OpenTabTool<BacktestViewModel, BacktestView>(BacktestTabId, "Backtest", "Loading the backtest workspace…");

    [RelayCommand]
    public void OpenResearch() =>
        OpenTabTool<FactorResearchViewModel, FactorResearchView>(ResearchTabId, "Factor research", "Loading factor research…");

    [RelayCommand]
    public void OpenRecorder() =>
        OpenTabTool<TickRecorderViewModel, TickRecorderView>(RecorderTabId, "Record ticks", "Preparing the tick recorder…");

    [RelayCommand]
    public void OpenMlFeatures() =>
        OpenTabTool<MlFeaturesViewModel, MlFeaturesView>(MlFeaturesTabId, "ML features", "Computing feature definitions…");

    [RelayCommand]
    public void OpenBacktestAnalysis() =>
        OpenTabTool<BacktestAnalysisViewModel, BacktestAnalysisView>(BacktestAnalysisTabId, "Backtest analysis", "Loading backtest analysis…");

    [RelayCommand]
    public void OpenAiAnalyst() =>
        OpenTabTool<AiAnalystViewModel, AiAnalystView>(AiAnalystTabId, "AI market analyst", "Connecting to the AI analyst…");

    [RelayCommand]
    public void OpenCorrelation() =>
        OpenWindowTool<CorrelationMatrixViewModel, CorrelationMatrixWindow>(
            CorrelationWindowId, "Correlation matrix", "Computing the correlation matrix…");

    [RelayCommand]
    public void OpenLiveCorrelation() =>
        OpenWindowTool<LiveCorrelationMatrixViewModel, LiveCorrelationMatrixWindow>(
            LiveCorrelationWindowId, "Live correlation matrix", "Wiring the live correlation feed…");

    /// <summary>Help → Support the developer. Routes through the shared prompt service so the window
    /// is single-instance whether opened here or auto-shown on launch.</summary>
    [RelayCommand]
    public void OpenSupport() =>
        _services.GetRequiredService<TradingTerminal.App.Support.ISupportPrompt>()
            .Show(Application.Current.MainWindow);

    [RelayCommand]
    public void OpenCharts() =>
        OpenWindowTool<ChartsViewModel, ChartsWindow>(ChartsWindowId, "Charts", "Starting the charting engine…");

    // ── QuantConnect / LEAN ─────────────────────────────────────────────────────────────────
    // One single-instance window with four tabs; each menu item deep-links to a tab index.
    [RelayCommand] public void OpenQuantConnectBacktest() => OpenQuantConnect(0);
    [RelayCommand] public void OpenQuantConnectProjects() => OpenQuantConnect(1);
    [RelayCommand] public void OpenQuantConnectData() => OpenQuantConnect(2);
    [RelayCommand] public void OpenQuantConnectSettings() => OpenQuantConnect(3);

    private void OpenQuantConnect(int tab)
    {
        if (_openWindows.TryGetValue(QuantConnectWindowId, out var existing))
        {
            if (existing.DataContext is QuantConnectViewModel evm) evm.SelectedTabIndex = tab;
            existing.Activate();
            return;
        }

        OpenWithOverlay("Opening QuantConnect / LEAN…", "Loading the LEAN workspace…", () =>
        {
            var vm = _services.GetRequiredService<QuantConnectViewModel>();
            vm.SelectedTabIndex = tab;
            var window = _services.GetRequiredService<QuantConnectWindow>();
            window.DataContext = vm;
            window.Owner = Application.Current.MainWindow;
            window.Closed += (_, _) =>
            {
                _openWindows.Remove(QuantConnectWindowId);
                vm.Dispose();
            };
            _openWindows[QuantConnectWindowId] = window;
            window.Show();
            _logger.LogInformation("Opened QuantConnect / LEAN window (tab {Tab})", tab);
        });
    }

    [RelayCommand]
    public void OpenOrderBook() =>
        OpenWindowTool<OrderBookViewModel, OrderBookWindow>(OrderBookWindowId, "Order book", "Wiring the depth-of-book feed…");

    [RelayCommand]
    public void OpenFootprint() =>
        OpenWindowTool<VolumeFootprintViewModel, VolumeFootprintWindow>(FootprintWindowId, "Volume footprint", "Preparing the volume-footprint grid…");

    [RelayCommand]
    public void OpenBookmap() =>
        OpenWindowTool<BookmapHeatmapViewModel, BookmapHeatmapWindow>(BookmapWindowId, "Bookmap + VolBook", "Building the liquidity heatmap…");

    [RelayCommand]
    public void OpenMarkovRegime() =>
        OpenTabTool<MarkovRegimeViewModel, MarkovRegimeView>(MarkovRegimeTabId, "Markov regime", "Building the Markov regime model…");

    [RelayCommand]
    public void OpenStationarity() =>
        OpenTabTool<StationarityViewModel, StationarityView>(StationarityTabId, "Stationarity & differencing", "Loading the time-series workspace…");

    [RelayCommand]
    public void OpenArimaGarch() =>
        OpenTabTool<ArimaGarchViewModel, ArimaGarchView>(ArimaGarchTabId, "ARIMA & GARCH", "Loading the time-series workspace…");

    [RelayCommand]
    public void OpenKalmanFilter() =>
        OpenTabTool<KalmanFilterViewModel, KalmanFilterView>(KalmanFilterTabId, "Kalman filter", "Loading the time-series workspace…");

    [RelayCommand]
    public void OpenAdvancedRegime() =>
        // The VM runs an auto-refresh loop — track it so it stops when the tab closes.
        OpenTabTool<AdvancedMarketRegimeViewModel, AdvancedMarketRegimeView>(
            AdvancedRegimeTabId, "Advanced market regime", "Running the regime indicator stack…", trackDisposable: true);

    [RelayCommand]
    public void OpenNotificationsSettings() =>
        OpenTabTool<NotificationsSettingsViewModel, NotificationsSettingsView>(NotificationsSettingsTabId, "Notifications", "Loading settings…");

    [RelayCommand]
    public void OpenArchiveSettings() =>
        OpenTabTool<ArchiveSettingsViewModel, ArchiveSettingsView>(ArchiveSettingsTabId, "Archive settings", "Loading settings…");

    [RelayCommand]
    public void OpenArchiveActivity() => OpenOrActivateArchiveHistory();

    private ArchiveActivityViewModel OpenOrActivateArchiveHistory()
    {
        var existing = OpenTabs.FirstOrDefault(t => t.ContentId == ArchiveActivityTabId);
        if (existing is not null)
        {
            ActiveTab = existing;
            return (ArchiveActivityViewModel)((FrameworkElement)existing.Content!).DataContext;
        }

        var vm = _services.GetRequiredService<ArchiveActivityViewModel>();
        var view = _services.GetRequiredService<ArchiveActivityView>();
        view.DataContext = vm;

        var tab = new DockTab
        {
            Title = "Archive history",
            ContentId = ArchiveActivityTabId,
            Content = view,
            CanClose = true,
        };
        OpenTabs.Add(tab);
        ActiveTab = tab;
        return vm;
    }

    /// <summary>Data → Instant offload: opens the Archive history view and immediately ships every
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
