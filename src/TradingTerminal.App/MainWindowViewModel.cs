using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.App.Ai;
using TradingTerminal.App.AiAnalyst;
using TradingTerminal.App.Backtest;
using TradingTerminal.App.Notifications;
using TradingTerminal.App.Recording;
using TradingTerminal.App.Regime;
using TradingTerminal.App.Research;
using TradingTerminal.App.Shell;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Events;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Session;
using TradingTerminal.Core.Strategies;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;

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
    private const string RegimeTabId = "tools.regime";

    private readonly IStrategyFactory _factory;
    private readonly IMarketDataRepository _repository;
    private readonly IEventBus _eventBus;
    private readonly SessionContext _session;
    private readonly IBrokerSelector _brokerSelector;
    private readonly IServiceProvider _services;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly DispatcherTimer _clockTimer;

    public MainWindowViewModel(
        IStrategyFactory factory,
        IMarketDataRepository repository,
        IEventBus eventBus,
        InMemoryLogSink logSink,
        SessionContext session,
        IBrokerSelector brokerSelector,
        IServiceProvider services,
        ILogger<MainWindowViewModel> logger)
    {
        _factory = factory;
        _repository = repository;
        _eventBus = eventBus;
        _session = session;
        _brokerSelector = brokerSelector;
        _services = services;
        _logger = logger;

        Strategies = new ObservableCollection<ITradingStrategy>(factory.All);
        OpenTabs = new ObservableCollection<DockTab>();
        _openWindows = new Dictionary<string, Window>(StringComparer.Ordinal);
        _tabDisposables = new Dictionary<DockTab, IDisposable>();
        LogSink = logSink;

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => CurrentTime = DateTime.Now.ToString("HH:mm:ss");
        _clockTimer.Start();

        // Mirror connection state into our own observable property.
        repository.ConnectionState.Subscribe(s => ConnectionState = s);

        _session.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(SessionUserDisplay));
            OnPropertyChanged(nameof(IsAuthenticated));
        };

        _brokerSelector.ActiveChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ModeDisplayName));
            OnPropertyChanged(nameof(IsLiveMode));
            OnPropertyChanged(nameof(ActiveBrokerLabel));
            OnPropertyChanged(nameof(DisconnectBannerText));
        };
    }

    private readonly Dictionary<string, Window> _openWindows;
    private readonly Dictionary<DockTab, IDisposable> _tabDisposables;

    public ObservableCollection<ITradingStrategy> Strategies { get; }
    public ObservableCollection<DockTab> OpenTabs { get; }
    public InMemoryLogSink LogSink { get; }

    public string ModeDisplayName => _brokerSelector.ActiveMode.DisplayName;
    public bool IsLiveMode => _brokerSelector.ActiveMode.IsLive;

    public string ActiveBrokerLabel => _brokerSelector.ActiveKind switch
    {
        BrokerKind.InteractiveBrokers => "Interactive Brokers",
        BrokerKind.NinjaTrader => "NinjaTrader",
        BrokerKind.CTrader => "cTrader",
        _ => _brokerSelector.ActiveKind.ToString(),
    };

    public string DisconnectBannerText => $"Disconnected from {ActiveBrokerLabel}";

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
        _logger.LogInformation("Reconnect requested by user");
        await _repository.ConnectAsync();
    }

    [RelayCommand]
    public void Exit()
    {
        System.Windows.Application.Current.Shutdown();
    }

    [RelayCommand]
    public void ShowStrategies() => IsStrategiesVisible = true;

    [RelayCommand]
    public void ShowLogs() => IsLogsVisible = true;

    [RelayCommand]
    public void OpenBacktest()
    {
        var existing = OpenTabs.FirstOrDefault(t => t.ContentId == BacktestTabId);
        if (existing is not null) { ActiveTab = existing; return; }

        var vm = _services.GetRequiredService<BacktestViewModel>();
        var view = _services.GetRequiredService<BacktestView>();
        view.DataContext = vm;

        var tab = new DockTab
        {
            Title = "Backtest",
            ContentId = BacktestTabId,
            Content = view,
            CanClose = true,
        };
        OpenTabs.Add(tab);
        ActiveTab = tab;
    }

    [RelayCommand]
    public void OpenResearch()
    {
        var existing = OpenTabs.FirstOrDefault(t => t.ContentId == ResearchTabId);
        if (existing is not null) { ActiveTab = existing; return; }

        var vm = _services.GetRequiredService<FactorResearchViewModel>();
        var view = _services.GetRequiredService<FactorResearchView>();
        view.DataContext = vm;

        var tab = new DockTab
        {
            Title = "Factor research",
            ContentId = ResearchTabId,
            Content = view,
            CanClose = true,
        };
        OpenTabs.Add(tab);
        ActiveTab = tab;
    }

    [RelayCommand]
    public void OpenRecorder()
    {
        var existing = OpenTabs.FirstOrDefault(t => t.ContentId == RecorderTabId);
        if (existing is not null) { ActiveTab = existing; return; }

        var vm = _services.GetRequiredService<TickRecorderViewModel>();
        var view = _services.GetRequiredService<TickRecorderView>();
        view.DataContext = vm;

        var tab = new DockTab
        {
            Title = "Record ticks",
            ContentId = RecorderTabId,
            Content = view,
            CanClose = true,
        };
        OpenTabs.Add(tab);
        ActiveTab = tab;
    }

    [RelayCommand]
    public void OpenMlFeatures()
    {
        var existing = OpenTabs.FirstOrDefault(t => t.ContentId == MlFeaturesTabId);
        if (existing is not null) { ActiveTab = existing; return; }

        var vm = _services.GetRequiredService<MlFeaturesViewModel>();
        var view = _services.GetRequiredService<MlFeaturesView>();
        view.DataContext = vm;

        var tab = new DockTab
        {
            Title = "ML features",
            ContentId = MlFeaturesTabId,
            Content = view,
            CanClose = true,
        };
        OpenTabs.Add(tab);
        ActiveTab = tab;
    }

    [RelayCommand]
    public void OpenBacktestAnalysis()
    {
        var existing = OpenTabs.FirstOrDefault(t => t.ContentId == BacktestAnalysisTabId);
        if (existing is not null) { ActiveTab = existing; return; }

        var vm = _services.GetRequiredService<BacktestAnalysisViewModel>();
        var view = _services.GetRequiredService<BacktestAnalysisView>();
        view.DataContext = vm;

        var tab = new DockTab
        {
            Title = "Backtest analysis",
            ContentId = BacktestAnalysisTabId,
            Content = view,
            CanClose = true,
        };
        OpenTabs.Add(tab);
        ActiveTab = tab;
    }

    [RelayCommand]
    public void OpenAiAnalyst()
    {
        var existing = OpenTabs.FirstOrDefault(t => t.ContentId == AiAnalystTabId);
        if (existing is not null) { ActiveTab = existing; return; }

        var vm = _services.GetRequiredService<AiAnalystViewModel>();
        var view = _services.GetRequiredService<AiAnalystView>();
        view.DataContext = vm;

        var tab = new DockTab
        {
            Title = "AI market analyst",
            ContentId = AiAnalystTabId,
            Content = view,
            CanClose = true,
        };
        OpenTabs.Add(tab);
        ActiveTab = tab;
    }

    [RelayCommand]
    public void OpenRegime()
    {
        var existing = OpenTabs.FirstOrDefault(t => t.ContentId == RegimeTabId);
        if (existing is not null) { ActiveTab = existing; return; }

        var vm = _services.GetRequiredService<MarketRegimeViewModel>();
        var view = _services.GetRequiredService<MarketRegimeView>();
        view.DataContext = vm;

        var tab = new DockTab
        {
            Title = "Market regime",
            ContentId = RegimeTabId,
            Content = view,
            CanClose = true,
        };
        OpenTabs.Add(tab);
        // The VM holds a live subscription to the regime stream — dispose it when the tab closes.
        _tabDisposables[tab] = vm;
        ActiveTab = tab;
    }

    [RelayCommand]
    public void OpenNotificationsSettings()
    {
        var existing = OpenTabs.FirstOrDefault(t => t.ContentId == NotificationsSettingsTabId);
        if (existing is not null) { ActiveTab = existing; return; }

        var vm = _services.GetRequiredService<NotificationsSettingsViewModel>();
        var view = _services.GetRequiredService<NotificationsSettingsView>();
        view.DataContext = vm;

        var tab = new DockTab
        {
            Title = "Notifications",
            ContentId = NotificationsSettingsTabId,
            Content = view,
            CanClose = true,
        };
        OpenTabs.Add(tab);
        ActiveTab = tab;
    }

    public async Task StartAsync()
    {
        try { await _repository.ConnectAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Initial connect failed"); }
    }
}
