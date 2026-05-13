using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.UI;

/// <summary>
/// Base view-model for "live signal mode" hosts. Each per-strategy project subclasses
/// this and overrides <see cref="BuildStrategy"/> to instantiate its underlying
/// <see cref="IBacktestStrategy"/> with strategy-specific parameters. The base owns the
/// tick subscription, the synthetic order router, the price-bar aggregation used for
/// chart visualisation, and notification publishing.
///
/// The host flow mirrors RSI / Cumulative Delta: user fills the setup form → presses
/// Continue (<see cref="IsConfigured"/> flips) → tick stream starts and price bars render
/// → optional Run/Stop arms the algo (<see cref="IsAlgoRunning"/>); notifications are
/// suppressed when not armed.
/// </summary>
public abstract partial class LiveSignalStrategyViewModelBase : ViewModelBase, IDisposable
{
    public const int MaxSignalsRetained = 200;
    public const int MaxBarsRetained = 300;

    private readonly IMarketDataRepository _repository;
    private readonly INotificationPublisher _notifications;
    private readonly ILogger _logger;
    private readonly IClock _clock;
    private readonly ISignalGeneratorRouterFactory _routerFactory;

    private CancellationTokenSource? _streamCts;
    private SignalGeneratorRouter? _router;
    private IBacktestStrategy? _strategy;
    private IDisposable? _eventSubscription;
    private DateTime _currentBarStart = DateTime.MinValue;
    private double _barOpen, _barHigh, _barLow, _barClose;
    private long _barVolume;
    private static readonly TimeSpan BarInterval = TimeSpan.FromSeconds(15);

    protected LiveSignalStrategyViewModelBase(
        string strategyId,
        string strategyDisplayName,
        IMarketDataRepository repository,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger logger)
    {
        StrategyId = strategyId;
        StrategyDisplayName = strategyDisplayName;
        _repository = repository;
        _notifications = notifications;
        _clock = clock;
        _routerFactory = routerFactory;
        _logger = logger;

        Instruments = SignalInstrumentCatalog.All;
        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                             ?? Instruments[0];
        Signals = new ObservableCollection<SignalEntry>();
        Bars = new ObservableCollection<Bar>();
    }

    public string StrategyId { get; }
    public string StrategyDisplayName { get; }
    public IReadOnlyList<SignalInstrument> Instruments { get; }
    public ObservableCollection<SignalEntry> Signals { get; }

    /// <summary>15-second price bars derived from the live tick stream. Used for chart drawing.</summary>
    public ObservableCollection<Bar> Bars { get; }

    public event EventHandler? BarsChanged;

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private string _status = "Configure the strategy to begin.";
    [ObservableProperty] private double? _lastBid;
    [ObservableProperty] private double? _lastAsk;
    [ObservableProperty] private double? _lastMid;
    [ObservableProperty] private long _ticksSeen;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string? _validationError;

    /// <summary>True once the user clicks Continue on the setup form.</summary>
    [ObservableProperty] private bool _isConfigured;

    /// <summary>True only while the user has armed the algo via Run. Display-only otherwise.</summary>
    [ObservableProperty] private bool _isAlgoRunning;

    /// <summary>Subclasses build a fresh <see cref="IBacktestStrategy"/> here using their
    /// current parameter property values.</summary>
    protected abstract IBacktestStrategy BuildStrategy(Contract contract);

    /// <summary>Override to validate strategy-specific parameters before
    /// <see cref="IsConfigured"/> flips. Return an error message or null.</summary>
    protected virtual string? ValidateSetup() => null;

    /// <summary>Override to refresh indicator series after each new bar.
    /// Default does nothing.</summary>
    protected virtual void OnBarsUpdated() { }

    [RelayCommand]
    private async Task ContinueAsync()
    {
        ValidationError = null;
        if (SelectedInstrument is null) { ValidationError = "Pick an instrument before continuing."; return; }
        var setupError = ValidateSetup();
        if (setupError is not null) { ValidationError = setupError; return; }

        IsConfigured = true;
        await StartAsync();
    }

    [RelayCommand]
    private void ToggleAlgo()
    {
        IsAlgoRunning = !IsAlgoRunning;
        var label = SelectedInstrument?.DisplayName ?? "(none)";
        _logger.LogInformation("{Strategy} algo {State} for {Symbol}",
            StrategyDisplayName, IsAlgoRunning ? "ARMED" : "STOPPED", label);
        Status = IsAlgoRunning
            ? $"Algo armed on {label} — {StrategyDisplayName}"
            : $"Streaming {label} — algo idle";
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        ValidationError = null;
        if (SelectedInstrument is null) { ValidationError = "Pick an instrument before starting."; return; }
        if (IsStreaming) return;

        _streamCts = new CancellationTokenSource();
        _strategy = BuildStrategy(SelectedInstrument.Contract);
        _router = _routerFactory.Create();
        _router.SignalEmitted += OnSignalEmitted;
        _eventSubscription = _router.OrderEvents.Subscribe(async evt =>
        {
            try { await _strategy.OnOrderEventAsync(evt, _streamCts.Token); }
            catch (Exception ex) { _logger.LogWarning(ex, "{Strategy} OnOrderEventAsync threw", StrategyId); }
        });

        Signals.Clear();
        Bars.Clear();
        _currentBarStart = DateTime.MinValue;
        TicksSeen = 0;
        IsStreaming = true;
        Status = $"Streaming {SelectedInstrument.DisplayName} — {StrategyDisplayName}";

        try
        {
            await _strategy.OnStartAsync(_clock, _router, _streamCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Strategy} OnStartAsync threw", StrategyId);
            Status = $"Failed to start: {ex.Message}";
            await StopAsync();
            return;
        }

        _ = RunStreamAsync(SelectedInstrument.Contract, _streamCts.Token);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!IsStreaming && _streamCts is null) return;

        _streamCts?.Cancel();
        try { if (_strategy is not null && _router is not null) await _strategy.OnEndAsync(_clock, _router, CancellationToken.None); }
        catch (Exception ex) { _logger.LogDebug(ex, "{Strategy} OnEndAsync threw", StrategyId); }

        _eventSubscription?.Dispose(); _eventSubscription = null;
        if (_router is not null) { _router.SignalEmitted -= OnSignalEmitted; _router = null; }
        _strategy = null;
        _streamCts?.Dispose(); _streamCts = null;
        IsStreaming = false;
        IsAlgoRunning = false;
        Status = "Stopped";
    }

    [RelayCommand]
    private void ClearSignals() => Signals.Clear();

    private async Task RunStreamAsync(Contract contract, CancellationToken ct)
    {
        try
        {
            await foreach (var tick in _repository.SubscribeTicksAsync(contract, ct))
            {
                if (_strategy is null || _router is null) break;
                LastBid = tick.Bid;
                LastAsk = tick.Ask;
                LastMid = (tick.Bid + tick.Ask) * 0.5;
                TicksSeen++;
                AggregateBar(tick);
                _router.UpdateMarketContext(tick);
                try { await _strategy.OnTickAsync(tick, _clock, _router, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "{Strategy} OnTickAsync threw", StrategyId); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Strategy} tick stream ended", StrategyId);
            Status = $"Stream ended: {ex.Message}";
        }
        finally
        {
            IsStreaming = false;
        }
    }

    private void AggregateBar(Tick tick)
    {
        var mid = (tick.Bid + tick.Ask) * 0.5;
        var ts = tick.TimestampUtc;
        var bucket = new DateTime(ts.Ticks - (ts.Ticks % BarInterval.Ticks), DateTimeKind.Utc);

        if (_currentBarStart == DateTime.MinValue)
        {
            _currentBarStart = bucket;
            _barOpen = _barHigh = _barLow = _barClose = mid;
            _barVolume = 1;
            return;
        }

        if (bucket != _currentBarStart)
        {
            var bar = new Bar(_currentBarStart, _barOpen, _barHigh, _barLow, _barClose, _barVolume);
            Bars.Add(bar);
            while (Bars.Count > MaxBarsRetained) Bars.RemoveAt(0);
            OnBarsUpdated();
            BarsChanged?.Invoke(this, EventArgs.Empty);

            _currentBarStart = bucket;
            _barOpen = _barHigh = _barLow = _barClose = mid;
            _barVolume = 1;
        }
        else
        {
            if (mid > _barHigh) _barHigh = mid;
            if (mid < _barLow) _barLow = mid;
            _barClose = mid;
            _barVolume++;
        }
    }

    private void OnSignalEmitted(SignalEntry entry)
    {
        Signals.Insert(0, entry);
        while (Signals.Count > MaxSignalsRetained) Signals.RemoveAt(Signals.Count - 1);

        if (!IsAlgoRunning) return;

        var symbol = SelectedInstrument?.DisplayName ?? "(none)";
        var direction = entry.Side == OrderSide.Buy ? "LONG" : "SHORT";
        var msg = $"{entry.SideText} {entry.Quantity} {entry.OrderType} @ {entry.Price:F4} (mid {entry.Mid:F4})";

        _notifications.PublishAsync(new StrategyNotification(
            Kind: NotificationKind.Signal,
            StrategyId: StrategyId,
            StrategyName: StrategyDisplayName,
            Symbol: symbol,
            Direction: direction,
            Message: msg,
            TimestampUtc: entry.TimestampUtc))
            .FireAndForgetSafe(_logger, $"signal publish {StrategyId}");
    }

    public void Dispose()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _eventSubscription?.Dispose();
    }
}
