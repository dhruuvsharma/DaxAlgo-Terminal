using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.App.Backtest;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Time;
using TradingTerminal.UI;

namespace TradingTerminal.App.Strategies.Signal;

/// <summary>
/// Hosts one <see cref="IBacktestStrategy"/> in "live signal" mode. Subscribes to live
/// ticks via <see cref="IMarketDataRepository"/>, runs <c>OnTickAsync</c> on each tick,
/// and forwards every order the strategy submits into <see cref="SignalGeneratorRouter"/>
/// — which both displays the signal in the grid and publishes a <see cref="StrategyNotification"/>
/// out to every transport (Telegram, Discord, …).
///
/// No order execution happens. This is the "tell me what the strategy would do" mode the
/// user wants while routing actual fills through a separate execution app.
/// </summary>
public sealed partial class LiveSignalStrategyViewModel : ViewModelBase, IDisposable
{
    public const int MaxSignalsRetained = 200;

    private readonly IMarketDataRepository _repository;
    private readonly INotificationPublisher _notifications;
    private readonly ILogger<LiveSignalStrategyViewModel> _logger;
    private readonly BacktestStrategyOption _option;
    private readonly IClock _clock = new SystemClock();

    private CancellationTokenSource? _streamCts;
    private SignalGeneratorRouter? _router;
    private IBacktestStrategy? _strategy;
    private IDisposable? _eventSubscription;

    public LiveSignalStrategyViewModel(
        BacktestStrategyOption option,
        IMarketDataRepository repository,
        INotificationPublisher notifications,
        ILogger<LiveSignalStrategyViewModel> logger)
    {
        _option = option;
        _repository = repository;
        _notifications = notifications;
        _logger = logger;

        Instruments = SignalInstrumentCatalog.All;
        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                             ?? Instruments[0];

        Signals = new ObservableCollection<SignalEntry>();
    }

    public string StrategyDisplayName => _option.DisplayName;
    public string StrategyId => _option.Id;

    public IReadOnlyList<SignalInstrument> Instruments { get; }

    public ObservableCollection<SignalEntry> Signals { get; }

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private string _status = "Pick an instrument and press Start.";
    [ObservableProperty] private double? _lastBid;
    [ObservableProperty] private double? _lastAsk;
    [ObservableProperty] private double? _lastMid;
    [ObservableProperty] private long _ticksSeen;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string? _validationError;

    [RelayCommand]
    private async Task StartAsync()
    {
        ValidationError = null;
        if (SelectedInstrument is null)
        {
            ValidationError = "Pick an instrument before starting.";
            return;
        }
        if (IsStreaming) return;

        _streamCts = new CancellationTokenSource();
        _strategy = _option.Build(SelectedInstrument.Contract);
        _router = new SignalGeneratorRouter();
        _router.SignalEmitted += OnSignalEmitted;
        _eventSubscription = _router.OrderEvents.Subscribe(async evt =>
        {
            try { await _strategy.OnOrderEventAsync(evt, _streamCts.Token); }
            catch (Exception ex) { _logger.LogWarning(ex, "{Strategy} OnOrderEventAsync threw", _option.Id); }
        });

        Signals.Clear();
        TicksSeen = 0;
        IsStreaming = true;
        Status = $"Streaming {SelectedInstrument.DisplayName} — {_option.DisplayName}";

        try
        {
            await _strategy.OnStartAsync(_clock, _router, _streamCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Strategy} OnStartAsync threw", _option.Id);
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
        catch (Exception ex) { _logger.LogDebug(ex, "{Strategy} OnEndAsync threw", _option.Id); }

        _eventSubscription?.Dispose(); _eventSubscription = null;
        if (_router is not null) { _router.SignalEmitted -= OnSignalEmitted; _router = null; }
        _strategy = null;
        _streamCts?.Dispose(); _streamCts = null;
        IsStreaming = false;
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

                _router.UpdateMarketContext(tick);

                try { await _strategy.OnTickAsync(tick, _clock, _router, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "{Strategy} OnTickAsync threw", _option.Id); }
            }
        }
        catch (OperationCanceledException) { /* expected on Stop */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Strategy} tick stream ended", _option.Id);
            Status = $"Stream ended: {ex.Message}";
        }
        finally
        {
            IsStreaming = false;
        }
    }

    private void OnSignalEmitted(SignalEntry entry)
    {
        Signals.Insert(0, entry);
        while (Signals.Count > MaxSignalsRetained) Signals.RemoveAt(Signals.Count - 1);

        var symbol = SelectedInstrument?.DisplayName ?? "(none)";
        var direction = entry.Side == OrderSide.Buy ? "LONG" : "SHORT";
        var msg = $"{entry.SideText} {entry.Quantity} {entry.OrderType} @ {entry.Price:F4} (mid {entry.Mid:F4})";

        _ = _notifications.PublishAsync(new StrategyNotification(
            Kind: NotificationKind.Signal,
            StrategyId: _option.Id,
            StrategyName: _option.DisplayName,
            Symbol: symbol,
            Direction: direction,
            Message: msg,
            TimestampUtc: entry.TimestampUtc));
    }

    public void Dispose()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _eventSubscription?.Dispose();
    }
}
