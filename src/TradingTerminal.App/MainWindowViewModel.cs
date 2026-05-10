using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Threading;
using AvalonDock.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingTerminal.App.Notifications;
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
        OpenTabs = new ObservableCollection<LayoutDocument>();
        _openWindows = new Dictionary<string, Window>(StringComparer.Ordinal);
        _tabDisposables = new Dictionary<LayoutDocument, IDisposable>();
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
    private readonly Dictionary<LayoutDocument, IDisposable> _tabDisposables;

    public ObservableCollection<ITradingStrategy> Strategies { get; }
    public ObservableCollection<LayoutDocument> OpenTabs { get; }
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
    private LayoutDocument? _activeTab;

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

        var tab = new LayoutDocument
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
    public void CloseTab(LayoutDocument? tab)
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
    public void OpenNotificationsSettings()
    {
        var existing = OpenTabs.FirstOrDefault(t => t.ContentId == NotificationsSettingsTabId);
        if (existing is not null) { ActiveTab = existing; return; }

        var vm = _services.GetRequiredService<NotificationsSettingsViewModel>();
        var view = _services.GetRequiredService<NotificationsSettingsView>();
        view.DataContext = vm;

        var tab = new LayoutDocument
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
