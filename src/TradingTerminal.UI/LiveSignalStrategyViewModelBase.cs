using System.Collections.ObjectModel;
using System.Threading.Channels;
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
/// quote + depth subscriptions, the synthetic order router, the price-bar aggregation used for
/// chart visualisation, and notification publishing.
///
/// Streaming routes through the canonical market-data pipeline: a single
/// <see cref="IMarketDataIngest.Subscribe"/> handle starts (or joins) the ref-counted broker
/// L1 pump for the selected instrument, and this VM observes <see cref="IMarketDataHub.Quotes"/>
/// / <see cref="IMarketDataHub.Depth"/> keyed by canonical <see cref="InstrumentId"/>. Quote
/// records are projected to the legacy <see cref="Tick"/> shape at the boundary so the
/// engine-side <see cref="IBacktestStrategy"/> contract stays unchanged. On start, the chart is
/// warmed from the local <see cref="IMarketDataStore"/> so users see context immediately even
/// before the first live tick lands.
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

    /// <summary>Cap on how many instruments the picker shows at once. The broker universe can be
    /// ~11k symbols (Alpaca); the search box narrows it, so we never bind the whole list.</summary>
    public const int MaxInstrumentsDisplayed = 500;

    /// <summary>Bar size pulled from the store to pre-populate the chart at start. Mismatches the
    /// 15-second live aggregation by design — the store doesn't carry sub-minute bars, and a
    /// minute of recent context is more useful than no context at all.</summary>
    private const BarSize WarmupBarSize = BarSize.OneMinute;

    /// <summary>How many warm-up bars to pull from the store on Start. Subclasses override to
    /// scale the warm-up to their analysis window (e.g. the APEX scalper pulls
    /// <c>max(WindowSize, MaxChartCandles)</c> so the per-indicator charts have immediate context).</summary>
    protected virtual int WarmupBarCount => 120;

    private readonly LiveStrategyHostServices _services;
    private readonly INotificationPublisher _notifications;
    private readonly ILogger _logger;
    private readonly IClock _clock;
    private readonly ISignalGeneratorRouterFactory _routerFactory;

    private CancellationTokenSource? _streamCts;
    private SignalGeneratorRouter? _router;
    private IBacktestStrategy? _strategy;
    private IDisposable? _eventSubscription;
    private IDisposable? _ingestHandle;
    private DateTime _currentBarStart = DateTime.MinValue;
    private double _barOpen, _barHigh, _barLow, _barClose;
    private long _barVolume;
    private static readonly TimeSpan BarInterval = TimeSpan.FromSeconds(15);

    protected LiveSignalStrategyViewModelBase(
        string strategyId,
        string strategyDisplayName,
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger logger)
    {
        StrategyId = strategyId;
        StrategyDisplayName = strategyDisplayName;
        _services = services;
        _notifications = notifications;
        _clock = clock;
        _routerFactory = routerFactory;
        _logger = logger;

        AllInstruments = SignalInstrumentCatalog.All;
        Instruments = new ObservableCollection<SignalInstrument>(
            AllInstruments.Take(MaxInstrumentsDisplayed));
        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                             ?? Instruments.FirstOrDefault();
        Signals = new ObservableCollection<SignalEntry>();
        Bars = new ObservableCollection<Bar>();

        // Replace the static fallback with the connected broker's tradable universe.
        // Fire-and-forget: the continuation resumes on the UI context (VM is built there).
        _ = LoadInstrumentsAsync();
    }

    public string StrategyId { get; }
    public string StrategyDisplayName { get; }

    /// <summary>Full tradable universe from the connected broker (or the static fallback);
    /// <see cref="InstrumentSearchText"/> filters this into <see cref="Instruments"/>.</summary>
    public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }

    public ObservableCollection<SignalEntry> Signals { get; }

    /// <summary>15-second price bars derived from the live tick stream. Used for chart drawing.</summary>
    public ObservableCollection<Bar> Bars { get; }

    public event EventHandler? BarsChanged;

    /// <summary>Fires after every live quote tick has been pushed through the strategy. Charts
    /// that need per-tick refresh (rather than per-bar) subscribe here and coalesce their
    /// redraws — see the Apex window's DispatcherTimer-throttled redraw loop. Fired on the UI
    /// thread.</summary>
    public event EventHandler? TickProcessed;

    /// <summary>Most recent depth-of-market snapshot for the active instrument, or null when
    /// the broker doesn't expose L2 (NinjaTrader, Alpaca, IB-not-yet-wired). Set from the
    /// depth pump on the UI thread; raised via <see cref="ObservablePropertyAttribute"/> so the
    /// order-book pane re-renders automatically when it changes.</summary>
    [ObservableProperty] private DepthSnapshot? _latestDepth;

    /// <summary>Instruments shown in the picker — a capped, search-filtered view of
    /// <see cref="AllInstruments"/>.</summary>
    [ObservableProperty] private ObservableCollection<SignalInstrument> _instruments = new();

    /// <summary>Free-text filter applied over <see cref="AllInstruments"/> (see the picker's search box).</summary>
    [ObservableProperty] private string _instrumentSearchText = string.Empty;

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

    // ---------- Chart axis controls (live, render-only) ----------
    // Shared by every base-backed strategy's price chart. These only affect how the chart is
    // drawn — never strategy state — so they're live-editable while streaming. Windows read them
    // in their redraw; a change re-raises BarsChanged so the chart refreshes immediately.

    /// <summary>How many trailing bars the price chart draws (X-axis zoom / window).</summary>
    [ObservableProperty] private int _chartBarsShown = 150;

    /// <summary>When true the price chart Y range auto-fits; otherwise it's pinned to
    /// [<see cref="YAxisMin"/>, <see cref="YAxisMax"/>].</summary>
    [ObservableProperty] private bool _yAutoScale = true;

    [ObservableProperty] private double _yAxisMin;
    [ObservableProperty] private double _yAxisMax;

    partial void OnChartBarsShownChanged(int value) => BarsChanged?.Invoke(this, EventArgs.Empty);
    partial void OnYAutoScaleChanged(bool value) => BarsChanged?.Invoke(this, EventArgs.Empty);
    partial void OnYAxisMinChanged(double value) { if (!YAutoScale) BarsChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnYAxisMaxChanged(double value) { if (!YAutoScale) BarsChanged?.Invoke(this, EventArgs.Empty); }

    /// <summary>
    /// Loads the connected broker's tradable instruments and swaps them in for the static
    /// fallback. cTrader yields FX pairs, Alpaca yields stocks + crypto, IB/NinjaTrader yield
    /// curated catalogs. On failure or an empty list we keep the static catalog already shown.
    /// </summary>
    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _services.Repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0) return;

            AllInstruments = list
                .Select(i => new SignalInstrument(
                    $"{i.DisplayName}  ·  {BrokerLabel(i.Broker)}",
                    i.Category,
                    i.Contract,
                    i.Broker))
                .ToList();

            // Prefer a familiar default if any broker offers it; otherwise the first symbol.
            SelectedInstrument = AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                                 ?? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == "AAPL")
                                 ?? AllInstruments.FirstOrDefault();
            ApplyInstrumentFilter();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Strategy} instrument list load failed; using static catalog", StrategyId);
        }
    }

    /// <summary>Short broker label appended to instrument rows so users can disambiguate the
    /// same ticker exposed by multiple connected brokers (e.g. "ES — IB" vs "ES — cTrader").</summary>
    private static string BrokerLabel(Core.Brokers.BrokerKind broker) => broker switch
    {
        Core.Brokers.BrokerKind.InteractiveBrokers => "IB",
        Core.Brokers.BrokerKind.NinjaTrader => "NinjaTrader",
        Core.Brokers.BrokerKind.CTrader => "cTrader",
        Core.Brokers.BrokerKind.Alpaca => "Alpaca",
        _ => broker.ToString(),
    };

    /// <summary>Resolves the broker to talk to for the selected instrument. Live picker rows carry
    /// their source broker; static-catalog rows (no broker connected yet) fall back to whichever
    /// broker is currently connected first. Throws if no broker is connected — the caller surfaces
    /// the message in <see cref="ValidationError"/> so the user can go connect one.</summary>
    private Core.Brokers.BrokerKind ResolveBroker(SignalInstrument instrument)
    {
        if (instrument.Broker is { } explicitBroker && _services.Selector.IsConnected(explicitBroker))
            return explicitBroker;
        var connected = _services.Selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect at least one broker in the login screen.");
        return connected[0];
    }

    partial void OnInstrumentSearchTextChanged(string value) => ApplyInstrumentFilter();

    /// <summary>Rebuilds <see cref="Instruments"/> from <see cref="AllInstruments"/> using the
    /// current search text, capped at <see cref="MaxInstrumentsDisplayed"/>. The selected item
    /// is preserved (and force-included) so the picker never blanks out mid-filter.</summary>
    private void ApplyInstrumentFilter()
    {
        var term = InstrumentSearchText?.Trim() ?? string.Empty;
        IEnumerable<SignalInstrument> query = AllInstruments;
        if (term.Length > 0)
            query = AllInstruments.Where(i =>
                i.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase));

        var shown = query.Take(MaxInstrumentsDisplayed).ToList();

        var keep = SelectedInstrument;
        if (keep is not null && !shown.Contains(keep)) shown.Insert(0, keep);

        Instruments = new ObservableCollection<SignalInstrument>(shown);
        SelectedInstrument = keep is not null && Instruments.Contains(keep)
            ? keep
            : Instruments.FirstOrDefault();
    }

    /// <summary>Subclasses build a fresh <see cref="IBacktestStrategy"/> here using their
    /// current parameter property values.</summary>
    protected abstract IBacktestStrategy BuildStrategy(Contract contract);

    /// <summary>Override to validate strategy-specific parameters before
    /// <see cref="IsConfigured"/> flips. Return an error message or null.</summary>
    protected virtual string? ValidateSetup() => null;

    /// <summary>Override to refresh indicator series after each new bar.
    /// Default does nothing.</summary>
    protected virtual void OnBarsUpdated() { }

    /// <summary>Called after <see cref="WarmUpBarsAsync"/> seeds <see cref="Bars"/> from the
    /// local store and before the live tick stream starts. Subclasses can pre-populate engine
    /// state from the same bars — e.g. the APEX scalper seeds its snapshot history so the
    /// price chart isn't blank until the first live candle rolls. Default does nothing.</summary>
    protected virtual Task OnWarmupBarsLoadedAsync(IReadOnlyList<Bar> bars) => Task.CompletedTask;

    /// <summary>Push one row onto the universal activity log, tagged with this strategy's display
    /// name as the source. Self-bounds and marshals to the UI thread inside the shared sink.</summary>
    protected void Log(string category, string message) =>
        _services.ActivityLog.Append(StrategyDisplayName, category, message);

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
        Log("ALGO", IsAlgoRunning ? $"ARMED on {label}" : $"DISARMED on {label}");
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        ValidationError = null;
        if (SelectedInstrument is null) { ValidationError = "Pick an instrument before starting."; return; }
        if (IsStreaming) return;

        Core.Brokers.BrokerKind broker;
        try { broker = ResolveBroker(SelectedInstrument); }
        catch (InvalidOperationException ex) { ValidationError = ex.Message; return; }

        var contract = SelectedInstrument.Contract;
        var instrumentId = _services.Ingest.Resolve(contract, broker);

        _streamCts = new CancellationTokenSource();
        _strategy = BuildStrategy(contract);
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
        Log("STREAM", $"Started {SelectedInstrument.DisplayName} ({contract.SecType} {contract.Exchange})");

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

        await WarmUpBarsAsync(instrumentId, _streamCts.Token);

        // Start (or join) the ref-counted L1 broker pump for this instrument. Quotes and depth
        // share the same handle on the ingest side, so a single Subscribe powers both observables.
        _ingestHandle = _services.Ingest.Subscribe(contract, broker);

        _ = RunQuoteStreamAsync(instrumentId, _streamCts.Token);
        _ = RunDepthStreamAsync(instrumentId, _streamCts.Token);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!IsStreaming && _streamCts is null) return;

        _streamCts?.Cancel();
        try { if (_strategy is not null && _router is not null) await _strategy.OnEndAsync(_clock, _router, CancellationToken.None); }
        catch (Exception ex) { _logger.LogDebug(ex, "{Strategy} OnEndAsync threw", StrategyId); }

        _ingestHandle?.Dispose(); _ingestHandle = null;
        _eventSubscription?.Dispose(); _eventSubscription = null;
        if (_router is not null) { _router.SignalEmitted -= OnSignalEmitted; _router = null; }
        _strategy = null;
        _streamCts?.Dispose(); _streamCts = null;
        IsStreaming = false;
        IsAlgoRunning = false;
        LatestDepth = null;
        Status = "Stopped";
        Log("STREAM", "Stopped");
    }

    [RelayCommand]
    private void ClearSignals() => Signals.Clear();

    /// <summary>Seeds <see cref="Bars"/> with the most recent 1-minute bars from the local store
    /// so the chart isn't empty when the user clicks Start. Granularity intentionally differs from
    /// the 15-second live aggregation that follows — the store has no sub-minute bars and recent
    /// 1-minute context is more useful than no context at all. Silent on failure.</summary>
    private async Task WarmUpBarsAsync(InstrumentId instrumentId, CancellationToken ct)
    {
        try
        {
            var recent = await _services.Store.GetRecentBarsAsync(instrumentId, WarmupBarSize, WarmupBarCount, ct);
            if (recent.Count == 0) return;
            foreach (var b in recent) Bars.Add(b.ToBar());
            while (Bars.Count > MaxBarsRetained) Bars.RemoveAt(0);
            await OnWarmupBarsLoadedAsync(Bars.ToList());
            OnBarsUpdated();
            BarsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Strategy} warm-up read from store failed", StrategyId);
        }
    }

    /// <summary>Pumps canonical quotes off the hub, projects them to the legacy <see cref="Tick"/>
    /// the strategy interface still speaks, and feeds each one to the strategy on the UI thread.
    /// A bounded channel decouples the hub's publish thread from the consumer so a slow strategy
    /// can't block ingest.</summary>
    private async Task RunQuoteStreamAsync(InstrumentId instrumentId, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<Tick>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        using var subscription = _services.Hub.Quotes(instrumentId).Subscribe(q =>
            channel.Writer.TryWrite(new Tick(q.EventTimeUtc, q.Bid, q.Ask, q.BidSize, q.AskSize)));

        try
        {
            await foreach (var tick in channel.Reader.ReadAllAsync(ct))
            {
                if (_strategy is null || _router is null) break;
                await UiThread.RunAsync(async () =>
                {
                    LastBid = tick.Bid;
                    LastAsk = tick.Ask;
                    LastMid = (tick.Bid + tick.Ask) * 0.5;
                    TicksSeen++;
                    AggregateBar(tick);
                    _router.UpdateMarketContext(tick);
                    try { await _strategy.OnTickAsync(tick, _clock, _router, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "{Strategy} OnTickAsync threw", StrategyId); }
                    TickProcessed?.Invoke(this, EventArgs.Empty);
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Strategy} tick stream ended", StrategyId);
            await UiThread.RunAsync(() => Status = $"Stream ended: {ex.Message}");
        }
        finally
        {
            channel.Writer.TryComplete();
            await UiThread.RunAsync(() => IsStreaming = false);
        }
    }

    /// <summary>
    /// Best-effort L2 depth pump running alongside the quote stream. Forwards each book
    /// snapshot to the strategy's <see cref="IBacktestStrategy.OnDepthAsync"/> so book-aware
    /// signals (OBI) can use real depth. Brokers without depth (Alpaca, IB-not-yet-wired)
    /// produce no events on the hub — we degrade silently to the L1 path.
    /// </summary>
    private async Task RunDepthStreamAsync(InstrumentId instrumentId, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<DepthSnapshot>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        using var subscription = _services.Hub.Depth(instrumentId).Subscribe(s =>
            channel.Writer.TryWrite(s));

        try
        {
            await foreach (var snapshot in channel.Reader.ReadAllAsync(ct))
            {
                if (_strategy is null || _router is null) break;
                await UiThread.RunAsync(async () =>
                {
                    LatestDepth = snapshot;
                    try { await _strategy.OnDepthAsync(snapshot, _clock, _router, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "{Strategy} OnDepthAsync threw", StrategyId); }
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Strategy} depth stream ended", StrategyId);
        }
        finally
        {
            channel.Writer.TryComplete();
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
            Log("BAR", $"{bar.TimestampUtc:HH:mm:ss} O={bar.Open:F4} H={bar.High:F4} L={bar.Low:F4} C={bar.Close:F4} V={bar.Volume}");
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

        Log("SIGNAL", $"{entry.SideText} {entry.Quantity} {entry.OrderType} @ {entry.Price:F4} (mid {entry.Mid:F4}){(IsAlgoRunning ? "" : " [idle]")}");

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
        _ingestHandle?.Dispose();
        _eventSubscription?.Dispose();
    }
}
