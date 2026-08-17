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
using TradingTerminal.Recording;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Events;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Session;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Updates;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.ExecutionUi;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;
using TradingTerminal.UI.Strategies;
using TradingTerminal.UI.Theming;
using TradingTerminal.UI.Updates;

namespace TradingTerminal.App;

public sealed partial class MainWindowViewModel : ViewModelBase, IShellOverlayPresenter
{
    // Stable per-window keys for the single-instance window registry (owned by IShellWindowHost).
    private const string NotificationsWindowId = "settings.notifications";
    private const string PluginManagerWindowId = "plugins.manager";
    private const string StrategyAuthoringWindowId = "authoring.strategy";
    private const string RecorderWindowId = "tools.recorder";
    private const string ArchiveSettingsWindowId = "settings.archive";
    private const string ArchiveActivityWindowId = "settings.archive.activity";
    private const string ThemeStudioWindowId = "settings.themestudio";
    private const string ExecutionConsoleWindowId = "tools.execution-console";

    private readonly IStrategyFactory _factory;
    private readonly IEventBus _eventBus;
    private readonly SessionContext _session;
    private readonly IBrokerSelector _brokerSelector;
    private readonly IServiceProvider _services;
    private readonly IShellWindowHost _host;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly DispatcherTimer _clockTimer;
    private readonly IThemeManager _themeManager;
    private readonly ICliWorkspaceLauncher? _cliLauncher;

    public MainWindowViewModel(
        IStrategyFactory factory,
        IEventBus eventBus,
        InMemoryLogSink logSink,
        SessionContext session,
        IBrokerSelector brokerSelector,
        BrokerApiMeterViewModel apiMeter,
        Infrastructure.Plugins.PluginHostContext pluginContext,
        IShellWindowHost host,
        IServiceProvider services,
        ILogger<MainWindowViewModel> logger,
        ICliWorkspaceLauncher? cliLauncher = null)
    {
        _factory = factory;
        _eventBus = eventBus;
        _session = session;
        _brokerSelector = brokerSelector;
        ApiMeter = apiMeter;
        PluginProblemCount = pluginContext.Report?.AttentionCount ?? 0;
        _host = host;
        _services = services;
        _logger = logger;
        // Resolved rather than ctor-injected: the recorder is an app-lifetime singleton the header
        // chip only observes, and this ctor is already at its parameter budget.
        Recorder = services.GetRequiredService<TickRecordingService>();
        // Same reasoning for the update strip. GetService, not GetRequiredService: a shell that
        // never calls AddUpdates should compose fine and simply never show the notice.
        Update = new UpdateNoticeViewModel(services.GetService<IUpdateNotifier>());

        // Vibe Code → Launch CLI: offer every agent CLI the app knows, tagged by whether it resolved on
        // PATH so the menu can show (and disable) an uninstalled one instead of hiding it.
        _cliLauncher = cliLauncher;
        var installedClis = cliLauncher?.AvailableClis() ?? [];
        CliLaunchChoices = AgentCliAdapter.All
            .Select(adapter => new CliLaunchChoice(adapter, installedClis.Contains(adapter)))
            .ToList();

        // Drive the shell "Opening…" curtain from the window host.
        _host.OverlayPresenter = this;

        Strategies = new ObservableCollection<ITradingStrategy>(factory.All);
        // Catalog rows: each strategy wrapped with its user presentation overrides (custom name /
        // description / tags / alpha formula / UI image). The list binds to these; the underlying
        // strategy stays reachable for Open and the pill converters.
        CatalogItems = new ObservableCollection<StrategyCatalogItemViewModel>(
            factory.All.Select(s => new StrategyCatalogItemViewModel(s)));
        // The Testing launch profile pairs a fixture strategy (seeded through the factory, so it is
        // already in the list above) with a fixture visualizer. Visualizer cards do not go through
        // IStrategyFactory - they are descriptors - so this one is added directly.
        if (services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TradingTerminal.Core.Configuration.DevOptions>>()
                .Value.SeedCatalogFixtures)
            CatalogItems.Add(new StrategyCatalogItemViewModel(DevCatalogSeed.FixtureVisualizer));
        // Strategies contributed by an UNSIGNED plugin (neither shipped-by-us nor from a pinned
        // publisher) wear the DEV badge on their catalog card, mirroring the Plugin Manager.
        _unsignedStrategyIds = System.Linq.Enumerable.ToHashSet(
            System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Where(factory.All,
                    s => pluginContext.UnsignedStrategyTypeNames.Contains(s.GetType().FullName ?? string.Empty)),
                s => s.Id),
            System.StringComparer.Ordinal);
        // A strategy registered while the app is running should appear immediately in both backing
        // collections. Runtime-authored strategies are unsigned, so add the badge id before the card.
        factory.Changed += (_, change) =>
        {
            void Apply()
            {
                _unsignedStrategyIds.Add(change.Strategy.Id);
                var existing = Strategies.FirstOrDefault(s => s.Id == change.Strategy.Id);
                if (existing is not null) Strategies[Strategies.IndexOf(existing)] = change.Strategy;
                else Strategies.Add(change.Strategy);

                var existingItem = CatalogItems.FirstOrDefault(i => i.Id == change.Strategy.Id);
                if (existingItem is not null)
                    CatalogItems[CatalogItems.IndexOf(existingItem)] = new StrategyCatalogItemViewModel(change.Strategy);
                else
                    CatalogItems.Add(new StrategyCatalogItemViewModel(change.Strategy));
            }

            if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())
                d.BeginInvoke(new Action(Apply));
            else
                Apply();
        };
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
        FeedDropCount = TradingTerminal.Infrastructure.Threading.FeedDropMeter.GlobalDropped;
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

    /// <summary>The catalog rows — each strategy wrapped with its user presentation overrides (custom
    /// name / description / tags / alpha formula / UI image), editable from the card's right-click menu.
    /// The list binds to this; <see cref="Strategies"/> stays the lookup for opening a strategy.</summary>
    public ObservableCollection<StrategyCatalogItemViewModel> CatalogItems { get; }

    /// <summary>Ids of strategies contributed by unsigned plugins or registered by the runtime
    /// authoring flow. The mutable backing set is updated before its catalog card is added.</summary>
    public System.Collections.Generic.IReadOnlySet<string> UnsignedStrategyIds => _unsignedStrategyIds;

    private readonly System.Collections.Generic.HashSet<string> _unsignedStrategyIds;
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

    /// <summary>Strategy plugins that failed to load or sit quarantined (user disables excluded) —
    /// drives the header warning chip; click-through opens the Plugin Manager.</summary>
    public int PluginProblemCount { get; }

    public bool HasPluginProblems => PluginProblemCount > 0;

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

    /// <summary>The selected catalog row. Bound to the list; it drives <see cref="SelectedStrategy"/> so
    /// the Open / Edit actions keep working off the underlying strategy.</summary>
    [ObservableProperty]
    private StrategyCatalogItemViewModel? _selectedCatalogItem;

    partial void OnSelectedCatalogItemChanged(StrategyCatalogItemViewModel? value) =>
        SelectedStrategy = value?.Strategy;

    /// <summary>Catalog card right-click → Edit: opens the modal editor for the selected strategy's
    /// presentation (name / tags / description / alpha formula / UI image). Saved overrides persist and
    /// refresh the card in place — they never touch the strategy's compiled code.</summary>
    [RelayCommand]
    private void EditStrategy()
    {
        if (SelectedCatalogItem is not { } item) return;
        StrategyPresentationEditor.ShowDialog(Application.Current.MainWindow, item);
    }

    [ObservableProperty]
    private ConnectionState _connectionState = Core.Domain.ConnectionState.Disconnected;

    [ObservableProperty]
    private string _currentTime = DateTime.Now.ToString("HH:mm:ss");

    /// <summary>Process-wide count of feed events shed by the bounded channel bridges
    /// (see FeedDropMeter). 0 in healthy sessions; sustained growth = a consumer can't keep
    /// up. Refreshed by the 1-second clock tick; the status bar shows it only when non-zero.</summary>
    [ObservableProperty] private long _feedDropCount;

    [ObservableProperty]
    private string _currentTimeUtc = DateTime.UtcNow.ToString("HH:mm:ss");

    [ObservableProperty]
    private bool _nyseOpen;

    [ObservableProperty]
    private bool _lseOpen;

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
            window.Owner = Application.Current.MainWindow;
            // Open full-size and remember the user's last size/position/state, keyed by strategy id.
            // Centralized here so every strategy window benefits regardless of its base class (the
            // StrategyWindowBase ones and the plain-MetroWindow cube/surface/regime ones alike).
            TradingTerminal.UI.StrategyWindowPlacementStore.Attach(window, capturedId);
            window.Closed += (_, _) =>
            {
                _host.Unregister(capturedId);
                // StrategyWindowBase owns its VM because it must await Stop before disposal. Plain
                // plugin windows and hosted views have no such owner, so the shell releases those.
                if (window is not StrategyWindowBase && host.ViewModel is IDisposable d) d.Dispose();
            };
            _host.Register(capturedId, window);
            window.Show();
            _eventBus.Publish(new StrategyOpenedEvent(host.StrategyId, host.DisplayName));
            _logger.LogInformation("Opened strategy window {Id} ({Name})", host.StrategyId, host.DisplayName);
        });
    }

    [RelayCommand]
    public void AddVisualizerToChart(string? visualizerId)
    {
        if (string.IsNullOrWhiteSpace(visualizerId))
            visualizerId = SelectedCatalogItem?.Visualizer?.Id;
        if (!string.IsNullOrWhiteSpace(visualizerId))
            _logger.LogWarning("Visualizer {VisualizerId} is not registered in this edition", visualizerId);
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

    /// <summary>File → Start QuestDB. Starts or attaches to the configured QuestDB runtime and
    /// re-arms the store so tick persistence engages without a restart.
    /// Progress shows in the activity log. No-op-with-a-message when QuestDB isn't the configured backend.</summary>
    [RelayCommand]
    public async Task StartQuestDbAsync()
    {
        var service = _services.GetRequiredService<IQuestDbLauncher>();
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
            PluginManagerWindowId, "Extensions", "Loading Extensions…");

    [RelayCommand]
    public void OpenStrategyAuthoring() =>
        _host.OpenHostedTool<TradingTerminal.App.Authoring.StrategyAuthoringViewModel, TradingTerminal.App.Authoring.StrategyAuthoringView>(
            StrategyAuthoringWindowId, "Hyperion", "Loading Hyperion…");

    /// <summary>The agent CLIs the "Launch CLI" menu offers — installed ones enabled, the rest shown
    /// disabled with an "install it" hint. Built once from what resolved on PATH at start.</summary>
    public IReadOnlyList<CliLaunchChoice> CliLaunchChoices { get; }

    /// <summary>True when at least one agent CLI is installed — lets a shell hide the launcher entirely
    /// where none is present, if it wants to.</summary>
    public bool HasCliLaunchers => CliLaunchChoices.Any(choice => choice.IsAvailable);

    /// <summary>Vibe Code → Launch CLI: scaffold a strategy-authoring workspace (CLAUDE.md / AGENTS.md,
    /// skills, hooks, system prompt, a starter project) and open the chosen CLI in a terminal there. The
    /// vendor CLI owns its own sign-in — no credentials pass through the app.</summary>
    [RelayCommand]
    public void LaunchCli(CliLaunchChoice? choice)
    {
        if (choice is null) return;
        if (_cliLauncher is null || !choice.IsAvailable)
        {
            LogSink.Append("Hyperion", "Warning",
                $"{choice.DisplayName} isn't available — install it and make sure it's on your PATH.");
            return;
        }

        var result = _cliLauncher.Launch(choice.Adapter, "vibe-scratch", "Vibe scratch strategy", StrategyBuildEffort.Standard);
        LogSink.Append("Hyperion", result.Success ? "Info" : "Warning", result.Message);
        _logger.LogInformation("Launched CLI {Cli}: {Message}", choice.DisplayName, result.Message);
    }


    /// <summary>The app-lifetime recording service, exposed so the header REC chip can light while a
    /// background recording is running. The panel window is only a view onto it — closing the panel
    /// does not stop the recording.</summary>

    /// <summary>
    /// Opens the lease-fenced execution console. Composed in every edition since 2026-08-17.
    ///
    /// <para>Opening it routes nothing: the app starts in Paper, where an order is recorded in the
    /// ledger and monitored here but never leaves the process. Real routing needs the login window's
    /// Paper/Real switch armed AND that exact broker account separately authorized.</para>
    /// </summary>
    [RelayCommand]
    public void OpenExecutionConsole() =>
        _host.OpenHostedTool<ExecutionConsoleViewModel, ExecutionConsoleView>(
            ExecutionConsoleWindowId,
            "Execution Engine",
            "Composing the lease-fenced execution console…",
            width: 1400,
            height: 840);

    public TickRecordingService Recorder { get; }

    /// <summary>Backs the "a new version is available" strip in the row-2 banner stack. Always
    /// present; it simply never becomes visible when no update feed is configured.</summary>
    public UpdateNoticeViewModel Update { get; }

    /// <summary>Header REC chip → the recorder panel. Small window: it's a watchlist + a toggle, not
    /// a workspace.</summary>
    [RelayCommand]
    public void OpenRecorder() =>
        _host.OpenHostedTool<RecorderPanelViewModel, RecorderPanelView>(
            RecorderWindowId, "Market data recorder", "Preparing the recorder…", width: 470, height: 600);

    /// <summary>Help → Support the developer. Routes through the shared prompt service so the window
    /// is single-instance whether opened here or auto-shown on launch.</summary>
    [RelayCommand]
    public void OpenSupport() =>
        _services.GetRequiredService<TradingTerminal.App.Support.ISupportPrompt>()
            .Show(Application.Current.MainWindow);

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

/// <summary>One entry in the Vibe Code → "Launch CLI" menu: an agent CLI (Claude Code / Codex) with
/// whether it was found on PATH. An unavailable one is shown disabled with an "install it" hint rather
/// than hidden, so the feature is discoverable before the CLI is set up.</summary>
public sealed class CliLaunchChoice(AgentCliAdapter adapter, bool isAvailable)
{
    public AgentCliAdapter Adapter { get; } = adapter;
    public bool IsAvailable { get; } = isAvailable;
    public string DisplayName => Adapter.DisplayName;
    public string MenuHeader => IsAvailable ? Adapter.DisplayName : $"{Adapter.DisplayName} — not installed";
}
