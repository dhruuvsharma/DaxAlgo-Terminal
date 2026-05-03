using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
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
    private readonly IStrategyFactory _factory;
    private readonly IMarketDataRepository _repository;
    private readonly IEventBus _eventBus;
    private readonly SessionContext _session;
    private readonly IbConnectionMode _connectionMode;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly DispatcherTimer _clockTimer;

    public MainWindowViewModel(
        IStrategyFactory factory,
        IMarketDataRepository repository,
        IEventBus eventBus,
        InMemoryLogSink logSink,
        SessionContext session,
        IbConnectionMode connectionMode,
        ILogger<MainWindowViewModel> logger)
    {
        _factory = factory;
        _repository = repository;
        _eventBus = eventBus;
        _session = session;
        _connectionMode = connectionMode;
        _logger = logger;

        Strategies = new ObservableCollection<ITradingStrategy>(factory.All);
        OpenTabs = new ObservableCollection<StrategyTabViewModel>();
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
    }

    public ObservableCollection<ITradingStrategy> Strategies { get; }
    public ObservableCollection<StrategyTabViewModel> OpenTabs { get; }
    public InMemoryLogSink LogSink { get; }

    public string ModeDisplayName => _connectionMode.DisplayName;
    public bool IsLiveMode => _connectionMode.IsLive;

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
    private StrategyTabViewModel? _activeTab;

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

        var existing = OpenTabs.FirstOrDefault(t => t.StrategyId == strategyId);
        if (existing is not null)
        {
            ActiveTab = existing;
            _logger.LogDebug("Focused existing tab {Id}", strategyId);
            return;
        }

        var host = _factory.Create(strategyId);
        var tab = new StrategyTabViewModel(host);
        OpenTabs.Add(tab);
        ActiveTab = tab;
        _eventBus.Publish(new StrategyOpenedEvent(host.StrategyId, host.DisplayName));
        _logger.LogInformation("Opened strategy tab {Id} ({Name})", host.StrategyId, host.DisplayName);
    }

    [RelayCommand]
    public void CloseTab(StrategyTabViewModel? tab)
    {
        if (tab is null) return;
        OpenTabs.Remove(tab);
        if (ReferenceEquals(ActiveTab, tab)) ActiveTab = OpenTabs.LastOrDefault();
        if (tab.ViewModel is IDisposable disposable) disposable.Dispose();
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

    public async Task StartAsync()
    {
        try { await _repository.ConnectAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Initial connect failed"); }
    }
}

public sealed class StrategyTabViewModel
{
    public StrategyTabViewModel(StrategyHost host)
    {
        StrategyId = host.StrategyId;
        DisplayName = host.DisplayName;
        View = host.View;
        ViewModel = host.ViewModel;
    }

    public string StrategyId { get; }
    public string DisplayName { get; }
    public object View { get; }
    public object ViewModel { get; }
}
