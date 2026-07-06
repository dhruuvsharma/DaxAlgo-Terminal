using System.Collections.ObjectModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.CumulativeDelta;

/// <summary>
/// View-model for the Cumulative Delta Scalper.
///
/// <para><b>Delta source (tape-primary).</b> When the broker supplies a trade tape the per-bar
/// delta is true aggressor-side volume (buy − sell contracts) and each closed bar also yields a
/// Core <see cref="FootprintBar"/> (volume-at-price clusters, POC, 3:1 stacked imbalances) for the
/// footprint panel and the footprint confirmation. When no tape ever arrives the engine falls back
/// to the original bid-tick uptick/downtick proxy (the cTrader-port behaviour) — the active mode
/// is surfaced as <see cref="FeedMode"/>.</para>
///
/// <para><b>Trigger.</b> Bar deltas are summed across a sliding window of N candles; a signal
/// candidate fires on the windowed cumulative delta crossing ±threshold. The threshold is
/// <b>adaptive</b> by default (<see cref="AutoThreshold"/>): θ = <see cref="ThresholdSigma"/> · σ
/// of the recent windowed-cumΔ distribution, so the same setting works on EURUSD ticks and MES
/// contracts alike; a fixed manual threshold remains available.</para>
///
/// <para><b>Confirmations (up to 6).</b> Momentum alignment (last 3 bar-deltas), HTF EMA(50) on
/// 15m, EMA slope, ADX(14) ≥ threshold, spread calm, and — tape only — footprint stacked-imbalance
/// agreement. Pre-signal guards (session window, spread cap, ATR band, cooldown, daily/session
/// caps) are evaluated every bar and published as <see cref="Gates"/> /
/// <see cref="Confirmations"/> rows so the UI can show exactly why the strategy is or isn't
/// firing. Spread/ATR gates are expressed in <b>basis points of price</b> — never absolute price
/// units — so they are instrument-agnostic.</para>
///
/// Display-only — no orders, no SL/TP, no lot sizing. Signals appear in the dashboard, the
/// log, and the notifier.
/// </summary>
public sealed partial class CumulativeDeltaViewModel : ViewModelBase, IDisposable
{
    public const int MaxBarsRetained = 300;
    public const int MaxWindowSize = 50;
    public const int MaxSpreadHistorySize = 200;

    /// <summary>Cap on instruments shown in the picker; the search box narrows the full universe.</summary>
    public const int MaxInstrumentsDisplayed = 500;

    private readonly LiveStrategyHostServices _services;
    private readonly INotificationPublisher _notifications;
    private readonly ILogger<CumulativeDeltaViewModel> _logger;

    private CancellationTokenSource? _cts;
    private IDisposable? _ticksHandle;
    private IDisposable? _chartBarsHandle;
    private IDisposable? _htfBarsHandle;
    private IDisposable? _tradesHandle;
    private double _prevBid;
    private bool _prevBidInitialised;

    // Tick-level uptick/downtick accounting for the in-progress bar (proxy mode only).
    private int _uptickCount;
    private int _downtickCount;

    // Trade-tape accounting for the in-progress bar (primary mode).
    private bool _sawRealTape;
    private double _priorTradePrice;
    private AggressorSide _priorTradeClass = AggressorSide.Unknown;
    private long _tapeBuyVolume;
    private long _tapeSellVolume;
    private readonly List<FootprintPrint> _barPrints = new(2_048);
    private long _cumulativeDeltaAll;

    private const int MaxFootprintBars = 8;
    private readonly List<FootprintBar> _footprintBars = new();

    /// <summary>Completed footprint bars (tape mode only), oldest first. Rendered by the window.</summary>
    public IReadOnlyList<FootprintBar> FootprintBars => _footprintBars;
    public event EventHandler? FootprintChanged;

    // Recent windowed-cumΔ values backing the adaptive threshold.
    private const int CumDeltaHistorySize = 80;
    private readonly List<int> _cumDeltaHistory = new(CumDeltaHistorySize);

    // Closed-bar circular buffer.
    private readonly int[] _circularBuffer = new int[MaxWindowSize];
    private int _bufferIndex;
    private int _bufferFilled;
    private int _previousCumDelta;

    // Spread-history circular buffer (per-bar samples in price units).
    private readonly double[] _spreadHistory = new double[MaxSpreadHistorySize];
    private int _spreadHistoryIdx;
    private int _spreadHistoryFilled;

    // Per-session bookkeeping.
    private SessionId _currentSession = SessionId.None;
    private int _sessionSignalCount;

    // Daily / cooldown bookkeeping.
    private DateTime _lastSignalDayUtc;
    private DateTime _lastSignalTimeUtc = DateTime.MinValue;

    /// <summary>Key under which this window remembers the last selected instrument (see
    /// <see cref="LastInstrumentStore"/>).</summary>
    private const string InstrumentPersistKey = "cumulative.delta";

    public CumulativeDeltaViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        ILogger<CumulativeDeltaViewModel> logger)
    {
        _services = services;
        _notifications = notifications;
        _logger = logger;

        AllInstruments = SignalInstrumentCatalog.All;
        // Hide-until-search + restore the last instrument used here (see InstrumentPickerFilter).
        Instruments = new ObservableCollection<SignalInstrument>();
        SelectedInstrument = InstrumentPickerFilter.InitialSelection(InstrumentPersistKey, AllInstruments,
            () => AllInstruments.FirstOrDefault(i => i.DisplayName.StartsWith("EUR.USD")) ?? AllInstruments.FirstOrDefault());
        ApplyInstrumentFilter();

        TimeframeOptions = new[]
        {
            BarSize.OneMinute,
            BarSize.ThreeMinutes,
            BarSize.FiveMinutes,
            BarSize.FifteenMinutes,
        };
        SelectedTimeframe = BarSize.ThreeMinutes;

        Bars = new ObservableCollection<Bar>();
        BarDeltas = new ObservableCollection<int>();
        RecentSignals = new ObservableCollection<string>();

        // Swap the static catalog for the connected broker's tradable universe.
        _ = LoadInstrumentsAsync();
    }

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _services.Repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0) return;

            // Broker is shown as a coloured pill by the dropdown — keep DisplayName clean.
            AllInstruments = list
                .Select(i => new SignalInstrument(i.DisplayName, i.Category, i.Contract, i.Broker))
                .ToList();

            // Prefer EUR.USD (the EA's home pair) if the broker offers FX; else the first symbol.
            SelectedInstrument = (SelectedInstrument?.Contract.Symbol is { } prev
                                     ? AllInstruments.FirstOrDefault(i => i.Contract.Symbol == prev) : null)
                                 ?? InstrumentPickerFilter.Remembered(InstrumentPersistKey, AllInstruments)
                                 ?? AllInstruments.FirstOrDefault(i => i.DisplayName.StartsWith("EUR.USD"))
                                 ?? AllInstruments.FirstOrDefault(i => i.Contract.Symbol.StartsWith("EURUSD"))
                                 ?? AllInstruments.FirstOrDefault();
            ApplyInstrumentFilter();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cumulative delta instrument list load failed; using static catalog");
        }
    }

    private BrokerKind ResolveBroker(SignalInstrument instrument)
    {
        if (instrument.Broker is { } explicitBroker && _services.Selector.IsConnected(explicitBroker))
            return explicitBroker;
        var connected = _services.Selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect at least one broker in the login screen.");
        return connected[0];
    }

    partial void OnInstrumentSearchTextChanged(string value) => ApplyInstrumentFilter();

    /// <summary>Hide-until-search: no term shows only the current selection; typing filters
    /// <see cref="AllInstruments"/>. Rebuilt in place so the selection never flickers out.</summary>
    private void ApplyInstrumentFilter() => InstrumentPickerFilter.Apply(
        Instruments,
        InstrumentPickerFilter.Visible(AllInstruments, InstrumentSearchText, SelectedInstrument, MaxInstrumentsDisplayed));

    // ---------- Setup form bindings ----------

    /// <summary>Full tradable universe from the connected broker (or the static fallback);
    /// <see cref="InstrumentSearchText"/> filters this into <see cref="Instruments"/>.</summary>
    public IReadOnlyList<SignalInstrument> AllInstruments { get; private set; }

    /// <summary>Instruments shown in the picker — a capped, search-filtered view of <see cref="AllInstruments"/>.</summary>
    [ObservableProperty] private ObservableCollection<SignalInstrument> _instruments = new();

    /// <summary>Free-text filter applied over <see cref="AllInstruments"/>.</summary>
    [ObservableProperty] private string _instrumentSearchText = string.Empty;

    public IReadOnlyList<BarSize> TimeframeOptions { get; }

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private BarSize _selectedTimeframe;

    [ObservableProperty] private int _windowSize = 10;

    /// <summary>Manual crossover threshold — used only when <see cref="AutoThreshold"/> is off.</summary>
    [ObservableProperty] private int _deltaThreshold = 300;

    /// <summary>Adaptive threshold: θ = <see cref="ThresholdSigma"/>·σ of recent windowed cumΔ.
    /// Makes the trigger scale-free across tick-proxy counts and real tape volume.</summary>
    [ObservableProperty] private bool _autoThreshold = true;
    [ObservableProperty] private double _thresholdSigma = 2.0;

    /// <summary>The threshold actually applied on the last closed bar (manual or adaptive).</summary>
    [ObservableProperty] private int _effectiveThreshold = 300;

    // Sniper filters
    [ObservableProperty] private int _minConfirmations = 3;
    [ObservableProperty] private int _emaSlopeBars = 3;
    [ObservableProperty] private double _adxThreshold = 18.0;
    [ObservableProperty] private double _spreadAvgMultiplier = 1.5;
    [ObservableProperty] private int _spreadHistorySize = 30;

    /// <summary>Hard spread cap in basis points of price (1 bp = 0.01%). ~1.4 bp ≈ the old
    /// 0.00015 EURUSD default, but works unchanged on futures/stocks.</summary>
    [ObservableProperty] private double _maxSpreadBp = 2.5;

    // Volatility gate — ATR(14) as basis points of price, instrument-agnostic.
    [ObservableProperty] private double _minAtrBp = 1.0;
    [ObservableProperty] private double _maxAtrBp = 50.0;

    // Session windows (GMT minute-of-day boundaries; users edit hours via the form)
    [ObservableProperty] private bool _useSessionFilter = true;
    [ObservableProperty] private bool _overlapOnly = false;

    [ObservableProperty] private int _overlapStartHour = 12;
    [ObservableProperty] private int _overlapStartMin  = 30;
    [ObservableProperty] private int _overlapEndHour   = 16;
    [ObservableProperty] private int _overlapEndMin    = 0;

    [ObservableProperty] private bool _useAsiaSession   = false;
    [ObservableProperty] private int _asiaStartHour = 0;
    [ObservableProperty] private int _asiaEndHour   = 7;

    [ObservableProperty] private bool _useLondonSession = true;
    [ObservableProperty] private int _londonStartHour = 7;
    [ObservableProperty] private int _londonEndHour   = 12;

    [ObservableProperty] private bool _useNewYorkSession = true;
    [ObservableProperty] private int _newYorkStartHour = 12;
    [ObservableProperty] private int _newYorkStartMin  = 30;
    [ObservableProperty] private int _newYorkEndHour   = 17;

    [ObservableProperty] private bool _useHtfFilter = true;
    [ObservableProperty] private int _maxSignalsPerSession = 4;
    [ObservableProperty] private int _maxDailySignals = 8;
    [ObservableProperty] private int _minSecondsBetweenSignals = 300;

    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private string? _validationError;

    // ---------- Runtime bindings ----------

    public ObservableCollection<Bar> Bars { get; }
    public ObservableCollection<int> BarDeltas { get; }
    public ObservableCollection<string> RecentSignals { get; }

    /// <summary>Per-closed-bar (time, barΔ, windowed cumΔ) points for the delta pane —
    /// shares the price chart's time axis.</summary>
    public ObservableCollection<DeltaPoint> DeltaPoints { get; } = new();

    /// <summary>Pre-signal gates evaluated on every closed bar — the "why is nothing firing" board.</summary>
    public ObservableCollection<GateRow> Gates { get; } = new();

    /// <summary>Confirmation checks evaluated each bar against the current cumΔ lean.</summary>
    public ObservableCollection<GateRow> Confirmations { get; } = new();

    [ObservableProperty] private string _status = "Configure the strategy to begin.";
    [ObservableProperty] private string _guardReason = "";

    /// <summary>"Real tape" once a genuine trade print arrives; "Bid-tick proxy" otherwise.</summary>
    [ObservableProperty] private string _feedMode = "—";

    /// <summary>6 with a real tape (footprint confirmation participates), 5 in proxy mode.</summary>
    public int MaxConfirmations => _sawRealTape ? 6 : 5;

    [ObservableProperty] private double? _lastBid;
    [ObservableProperty] private double? _lastAsk;
    [ObservableProperty] private double? _lastSpread;
    [ObservableProperty] private double? _avgSpread;
    [ObservableProperty] private int _liveDelta;
    [ObservableProperty] private int _cumulativeDelta;
    [ObservableProperty] private double? _lastAtr;
    [ObservableProperty] private double? _lastEmaHtf;
    [ObservableProperty] private double? _lastAdx;
    [ObservableProperty] private SessionId _activeSession = SessionId.None;
    [ObservableProperty] private int _lastConfirmationScore;

    [ObservableProperty] private int _todaySignalCount;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private bool _isAlgoRunning;

    // ---------- Chart axis controls (live, render-only) ----------
    // These only affect how the price plot is drawn — they never touch strategy state — so they
    // are always live. The Window's RedrawPrice reads them; changes re-raise BarsChanged to redraw.
    [ObservableProperty] private int _chartBarsShown = 120;
    [ObservableProperty] private bool _priceAutoScale = true;
    [ObservableProperty] private double _priceAxisMin;
    [ObservableProperty] private double _priceAxisMax;

    partial void OnChartBarsShownChanged(int value) => BarsChanged?.Invoke(this, EventArgs.Empty);
    partial void OnPriceAutoScaleChanged(bool value) => BarsChanged?.Invoke(this, EventArgs.Empty);
    partial void OnPriceAxisMinChanged(double value) { if (!PriceAutoScale) BarsChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnPriceAxisMaxChanged(double value) { if (!PriceAutoScale) BarsChanged?.Invoke(this, EventArgs.Empty); }

    public event EventHandler? BarsChanged;
    public event EventHandler? DeltasChanged;

    // 15m bar cache used by EMA + slope + ADX.
    private readonly List<Bar> _htfBars = new();

    // ---------- Setup → start ----------

    [RelayCommand]
    private void Continue()
    {
        ValidationError = null;

        if (SelectedInstrument is null)
            { ValidationError = "Pick an instrument before continuing."; return; }
        if (WindowSize is < 2 or > MaxWindowSize)
            { ValidationError = $"Window size must be between 2 and {MaxWindowSize}."; return; }
        if (DeltaThreshold <= 0)
            { ValidationError = "Delta threshold must be positive."; return; }
        if (MinConfirmations is < 0 or > 6)
            { ValidationError = "Min confirmations must be between 0 and 6."; return; }
        if (SpreadHistorySize is < 1 or > MaxSpreadHistorySize)
            { ValidationError = $"Spread history size must be between 1 and {MaxSpreadHistorySize}."; return; }
        if (MinAtrBp < 0 || MaxAtrBp <= MinAtrBp)
            { ValidationError = "Max ATR (bp) must be greater than Min ATR (bp), both ≥ 0."; return; }

        IsConfigured = true;
        _ = StartStreamAsync();
    }

    [RelayCommand]
    private void ToggleAlgo()
    {
        IsAlgoRunning = !IsAlgoRunning;
        var label = SelectedInstrument?.DisplayName ?? "(none)";
        _logger.LogInformation("CumulativeDelta sniper {State} for {Symbol}",
            IsAlgoRunning ? "ARMED" : "STOPPED", label);
        Status = IsAlgoRunning
            ? $"Sniper armed on {label} — minConf {MinConfirmations}/{MaxConfirmations}"
            : $"Streaming {label} — sniper idle";
    }

    public async Task StartStreamAsync()
    {
        if (IsStreaming || SelectedInstrument is null) return;

        BrokerKind broker;
        try { broker = ResolveBroker(SelectedInstrument); }
        catch (InvalidOperationException ex) { Status = ex.Message; return; }

        var contract = SelectedInstrument.Contract;
        var chartTf = SelectedTimeframe;

        Bars.Clear();
        BarDeltas.Clear();
        ResetState();
        BarsChanged?.Invoke(this, EventArgs.Empty);
        DeltasChanged?.Invoke(this, EventArgs.Empty);

        Status = $"Loading {SelectedInstrument.DisplayName} history…";

        try
        {
            var history = await _services.Repository.GetHistoricalBarsAsync(contract, broker, chartTf, TimeSpan.FromDays(1));
            foreach (var b in history.TakeLast(MaxBarsRetained))
                Bars.Add(b);
            RecalculateAtr();
            BarsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CumulativeDelta history backfill failed");
            Status = $"History fetch failed: {ex.Message}";
            return;
        }

        _cts = new CancellationTokenSource();
        IsStreaming = true;
        FeedMode = "Bid-tick proxy (no tape yet)";
        Status = $"Streaming {SelectedInstrument.DisplayName} — sniper idle";

        _ = RunTicksAsync(contract, broker, _cts.Token);
        _ = RunTradesAsync(contract, broker, _cts.Token);
        _ = RunChartBarsAsync(contract, broker, chartTf, _cts.Token);
        _ = RunHtfBarsAsync(contract, broker, _cts.Token);
    }

    public async Task StopStreamAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _ticksHandle?.Dispose(); _ticksHandle = null;
        _chartBarsHandle?.Dispose(); _chartBarsHandle = null;
        _htfBarsHandle?.Dispose(); _htfBarsHandle = null;
        _tradesHandle?.Dispose(); _tradesHandle = null;
        IsStreaming = false;
        IsAlgoRunning = false;
        Status = "Stopped";
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        // Remember the instrument the user was last on so this window reopens on it.
        LastInstrumentStore.Save(InstrumentPersistKey, SelectedInstrument?.Contract.Symbol);
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _ticksHandle?.Dispose(); _ticksHandle = null;
        _chartBarsHandle?.Dispose(); _chartBarsHandle = null;
        _htfBarsHandle?.Dispose(); _htfBarsHandle = null;
        _tradesHandle?.Dispose(); _tradesHandle = null;
    }

    // ---------- Tick stream (bid-tick rule) ----------

    // Hub pumps use bounded, DropOldest channels + batch draining (one UI marshal per drained
    // batch, not per event) so a fast feed the UI can't keep up with is capped in memory rather
    // than piling an unbounded backlog into GBs over a long session.
    private const int QuoteChannelCapacity = 16_384;
    private const int TradeChannelCapacity = 65_536;
    private const int BarChannelCapacity = 1_024;
    private const int MaxStreamDrainBatch = 4_096;

    private async Task RunTicksAsync(Contract contract, BrokerKind broker, CancellationToken ct)
    {
        // Canonical pipeline: subscribe to hub.Quotes, project back to Tick (preserves the
        // bid-tick-rule logic in ProcessTick), and start (or join) the L1 broker pump.
        var instrumentId = _services.Ingest.Resolve(contract, broker);
        var channel = Channel.CreateBounded<Tick>(new BoundedChannelOptions(QuoteChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        using var subscription = _services.Hub.Quotes(instrumentId).Subscribe(q =>
            channel.Writer.TryWrite(new Tick(q.EventTimeUtc, q.Bid, q.Ask, q.BidSize, q.AskSize)));
        _ticksHandle = _services.Ingest.Subscribe(contract, broker);

        var batch = new List<Tick>(256);
        try
        {
            while (await channel.Reader.WaitToReadAsync(ct))
            {
                batch.Clear();
                while (batch.Count < MaxStreamDrainBatch && channel.Reader.TryRead(out var t)) batch.Add(t);
                if (batch.Count == 0) continue;
                await UiThread.RunAsync(() => { foreach (var tick in batch) ProcessTick(tick); });
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tick stream ended");
            await UiThread.RunAsync(() => Status = $"Tick stream stopped: {ex.Message}");
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private void ProcessTick(Tick tick)
    {
        LastBid = tick.Bid;
        LastAsk = tick.Ask;
        LastSpread = tick.Ask - tick.Bid;

        // With a real tape the quote stream only refreshes bid/ask/spread — delta comes from prints.
        if (_sawRealTape) return;

        if (!_prevBidInitialised)
        {
            _prevBid = tick.Bid;
            _prevBidInitialised = true;
            return;
        }

        if (tick.Bid > _prevBid)      _uptickCount++;
        else if (tick.Bid < _prevBid) _downtickCount++;
        _prevBid = tick.Bid;
        LiveDelta = _uptickCount - _downtickCount;
    }

    // ---------- Trade-tape stream (primary delta source + footprint) ----------

    private async Task RunTradesAsync(Contract contract, BrokerKind broker, CancellationToken ct)
    {
        var instrumentId = _services.Ingest.Resolve(contract, broker);
        var channel = Channel.CreateBounded<TradePrint>(new BoundedChannelOptions(TradeChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        using var subscription = _services.Hub.Trades(instrumentId).Subscribe(t =>
            channel.Writer.TryWrite(t));
        // No-op handle on brokers without a tape — the channel simply never produces.
        _tradesHandle = _services.Ingest.SubscribeTrades(contract, broker);

        var batch = new List<TradePrint>(256);
        try
        {
            while (await channel.Reader.WaitToReadAsync(ct))
            {
                batch.Clear();
                while (batch.Count < MaxStreamDrainBatch && channel.Reader.TryRead(out var t)) batch.Add(t);
                if (batch.Count == 0) continue;
                await UiThread.RunAsync(() => { foreach (var trade in batch) ProcessTrade(trade); });
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trade-tape stream ended");
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private void ProcessTrade(TradePrint trade)
    {
        if (!_sawRealTape)
        {
            _sawRealTape = true;
            FeedMode = "Real tape";
            OnPropertyChanged(nameof(MaxConfirmations));
            _logger.LogInformation("[CumulativeDelta] Real trade tape detected — switching delta to aggressor volume");
        }

        var aggressor = trade.Aggressor;
        if (aggressor == AggressorSide.Unknown && LastBid is { } bid && LastAsk is { } ask && ask > bid)
            aggressor = Microstructure.ClassifyAggressor(trade.Price, bid, ask, _priorTradePrice, _priorTradeClass);
        _priorTradePrice = trade.Price;
        if (aggressor != AggressorSide.Unknown) _priorTradeClass = aggressor;

        _barPrints.Add(new FootprintPrint(trade.Price, trade.Size, aggressor, trade.EventTimeUtc));

        if (aggressor == AggressorSide.Buy) _tapeBuyVolume += trade.Size;
        else if (aggressor == AggressorSide.Sell) _tapeSellVolume += trade.Size;
        else
        {
            var half = trade.Size / 2;
            _tapeBuyVolume += trade.Size - half;
            _tapeSellVolume += half;
        }
        LiveDelta = ClampToInt(_tapeBuyVolume - _tapeSellVolume);
    }

    private static int ClampToInt(long v) => (int)Math.Clamp(v, int.MinValue, int.MaxValue);

    // ---------- Chart-TF bar stream: finalise bars, drive signals ----------

    private async Task RunChartBarsAsync(Contract contract, BrokerKind broker, BarSize chartTf, CancellationToken ct)
    {
        var instrumentId = _services.Ingest.Resolve(contract, broker);
        var channel = Channel.CreateBounded<Bar>(new BoundedChannelOptions(BarChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        using var subscription = _services.Hub.Bars(instrumentId, chartTf).Subscribe(b =>
            channel.Writer.TryWrite(b.ToBar()));
        _chartBarsHandle = _services.Ingest.SubscribeBars(contract, broker, chartTf);

        try
        {
            await foreach (var bar in channel.Reader.ReadAllAsync(ct))
            {
                await UiThread.RunAsync(() =>
                {
                    AppendBar(bar);
                    OnBarClosed();
                    BarsChanged?.Invoke(this, EventArgs.Empty);
                });
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chart bar stream ended");
            await UiThread.RunAsync(() => Status = $"Bar stream stopped: {ex.Message}");
        }
        finally
        {
            channel.Writer.TryComplete();
            await UiThread.RunAsync(() => IsStreaming = false);
        }
    }

    private void AppendBar(Bar bar)
    {
        Bars.Add(bar);
        while (Bars.Count > MaxBarsRetained) Bars.RemoveAt(0);
        RecalculateAtr();
    }

    private void OnBarClosed()
    {
        var w = Math.Clamp(WindowSize, 2, MaxWindowSize);
        var closedBar = Bars.Count > 0 ? Bars[^1] : null;

        // Tape-primary: aggressor-volume delta + a footprint bar; proxy: bid-tick counts.
        int candleDelta;
        if (_sawRealTape)
        {
            candleDelta = ClampToInt(_tapeBuyVolume - _tapeSellVolume);
            CompleteFootprintBar(closedBar);
        }
        else
        {
            candleDelta = _uptickCount - _downtickCount;
        }

        _circularBuffer[_bufferIndex] = candleDelta;
        _bufferIndex = (_bufferIndex + 1) % w;
        if (_bufferFilled < w) _bufferFilled++;

        BarDeltas.Clear();
        var start = (_bufferFilled >= w) ? _bufferIndex : 0;
        for (var i = 0; i < _bufferFilled; i++)
            BarDeltas.Add(_circularBuffer[(start + i) % w]);

        var cumDelta = SumWindow(w);
        CumulativeDelta = cumDelta;

        _cumDeltaHistory.Add(cumDelta);
        while (_cumDeltaHistory.Count > CumDeltaHistorySize) _cumDeltaHistory.RemoveAt(0);
        EffectiveThreshold = ComputeEffectiveThreshold();

        DeltaPoints.Add(new DeltaPoint(closedBar?.TimestampUtc ?? DateTime.UtcNow, candleDelta, cumDelta));
        while (DeltaPoints.Count > MaxBarsRetained) DeltaPoints.RemoveAt(0);
        DeltasChanged?.Invoke(this, EventArgs.Empty);

        // Sample once per bar.
        if (LastSpread is { } s)
        {
            _spreadHistory[_spreadHistoryIdx] = s;
            _spreadHistoryIdx = (_spreadHistoryIdx + 1) % Math.Max(1, SpreadHistorySize);
            if (_spreadHistoryFilled < SpreadHistorySize) _spreadHistoryFilled++;
            AvgSpread = AverageSpread();
        }

        _uptickCount = 0;
        _downtickCount = 0;
        _tapeBuyVolume = 0;
        _tapeSellVolume = 0;
        LiveDelta = 0;

        UpdateSessionState();
        ResetDailyCountersIfNeeded();
        UpdateGateBoard();
        UpdateConfirmationBoard(Math.Sign(cumDelta));

        if (_bufferFilled < w)
        {
            _previousCumDelta = cumDelta;
            return;
        }

        var crossover = ClassifyCrossover(cumDelta, _previousCumDelta);
        _previousCumDelta = cumDelta;

        if (crossover == 0) return;

        EvaluateSignal(crossover, cumDelta);
    }

    /// <summary>Builds the closed bar's footprint via Core (volume-at-price, POC, 3:1 stacks).</summary>
    private void CompleteFootprintBar(Bar? closedBar)
    {
        if (_barPrints.Count == 0) return;

        var startUtc = closedBar?.TimestampUtc ?? _barPrints[0].TimeUtc;
        var endUtc = startUtc + TimeframeSpan(SelectedTimeframe);
        var row = FootprintRowSize();
        var fp = FootprintFeatures.BuildBar(_barPrints, row, startUtc, endUtc, FeedQuality.RealTape, _cumulativeDeltaAll);
        _cumulativeDeltaAll = fp.CumulativeDelta;

        _footprintBars.Add(fp);
        while (_footprintBars.Count > MaxFootprintBars) _footprintBars.RemoveAt(0);
        _barPrints.Clear();
        FootprintChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Row size from the chart-TF ATR snapped to a 1-2-5 ladder (instrument-agnostic).</summary>
    private double FootprintRowSize()
    {
        var atr = LastAtr ?? 0;
        if (atr <= 0 && _barPrints.Count > 1)
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (var p in _barPrints) { if (p.Price < min) min = p.Price; if (p.Price > max) max = p.Price; }
            atr = max - min;
        }
        if (atr <= 0) return 0.0001;

        var raw = atr / 12.0;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var norm = raw / mag;
        var snap = norm < 1.5 ? 1.0 : norm < 3.5 ? 2.0 : norm < 7.5 ? 5.0 : 10.0;
        return snap * mag;
    }

    private static TimeSpan TimeframeSpan(BarSize size) => size switch
    {
        BarSize.OneMinute => TimeSpan.FromMinutes(1),
        BarSize.ThreeMinutes => TimeSpan.FromMinutes(3),
        BarSize.FiveMinutes => TimeSpan.FromMinutes(5),
        BarSize.FifteenMinutes => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(1),
    };

    private int ComputeEffectiveThreshold()
    {
        if (!AutoThreshold) return Math.Max(1, DeltaThreshold);
        if (_cumDeltaHistory.Count < 10) return Math.Max(1, DeltaThreshold);

        double mean = 0;
        foreach (var v in _cumDeltaHistory) mean += v;
        mean /= _cumDeltaHistory.Count;
        double acc = 0;
        foreach (var v in _cumDeltaHistory) { var d = v - mean; acc += d * d; }
        var sd = Math.Sqrt(acc / _cumDeltaHistory.Count);
        return Math.Max(1, (int)Math.Ceiling(Math.Max(0.5, ThresholdSigma) * sd));
    }

    private void EvaluateSignal(int crossover, int cumDelta)
    {
        var symbol = SelectedInstrument?.DisplayName ?? "(none)";
        var direction = DirectionLabel(crossover);

        if (!CheckPreSignalGuards(out var reason))
        {
            GuardReason = reason;
            return;
        }

        var conf = CountConfirmations(crossover);
        LastConfirmationScore = conf;
        var maxConf = MaxConfirmations;

        if (conf < MinConfirmations)
        {
            GuardReason = $"CONF {conf}/{maxConf} (need {MinConfirmations})";
            // Record a low-conf attempt as an idle signal so the user can tune the bar.
            PushSignalLine($"{DateTime.Now:HH:mm:ss}  (idle)  {direction} cumΔ={cumDelta} conf={conf}/{maxConf}");
            _notifications.PublishAsync(new StrategyNotification(
                Kind: NotificationKind.IdleSignal,
                StrategyId: "cumulative.delta.scalper",
                StrategyName: "Cumulative Delta",
                Symbol: symbol,
                Direction: direction,
                Message: $"(idle) {direction} cumΔ={cumDelta} conf={conf}/{maxConf}",
                TimestampUtc: DateTime.UtcNow))
                .FireAndForgetSafe(_logger, "cumdelta low-conf idle");
            return;
        }

        GuardReason = $"ACTIVE {_currentSession}";

        if (!IsAlgoRunning)
        {
            PushSignalLine($"{DateTime.Now:HH:mm:ss}  (idle)  {direction} cumΔ={cumDelta} conf={conf}/{maxConf}");
            _notifications.PublishAsync(new StrategyNotification(
                Kind: NotificationKind.IdleSignal,
                StrategyId: "cumulative.delta.scalper",
                StrategyName: "Cumulative Delta",
                Symbol: symbol,
                Direction: direction,
                Message: $"(idle) {direction} cumΔ={cumDelta} conf={conf}/{maxConf}",
                TimestampUtc: DateTime.UtcNow))
                .FireAndForgetSafe(_logger, "cumdelta unarmed idle");
            return;
        }

        TodaySignalCount++;
        _sessionSignalCount++;
        _lastSignalTimeUtc = DateTime.UtcNow;

        var line = $"{DateTime.Now:HH:mm:ss}  ARMED  {direction} cumΔ={cumDelta} conf={conf}/{maxConf} sess={_currentSession}";
        PushSignalLine(line);
        _logger.LogInformation("[CumulativeDelta] SNIPER {Direction} cumDelta={CumDelta} conf={Conf}/{MaxConf} session={Session}",
            direction, cumDelta, conf, maxConf, _currentSession);

        _notifications.PublishAsync(new StrategyNotification(
            Kind: NotificationKind.Signal,
            StrategyId: "cumulative.delta.scalper",
            StrategyName: "Cumulative Delta",
            Symbol: symbol,
            Direction: direction,
            Message: $"SNIPER {direction}  cumΔ={cumDelta}  conf={conf}/{maxConf}  session={_currentSession}",
            TimestampUtc: DateTime.UtcNow))
            .FireAndForgetSafe(_logger, "cumdelta sniper signal");
    }

    // ---------- Confirmations ----------

    private int CountConfirmations(int signal)
    {
        var c = 0;
        if (CheckMomentumAlignment(signal)) c++;
        if (CheckHtfEma(signal))            c++;
        if (CheckEmaSlope(signal))          c++;
        if (CheckAdxTrending())             c++;
        if (CheckSpread())                  c++;
        if (_sawRealTape && CheckFootprint(signal)) c++;
        return c;
    }

    /// <summary>Footprint confirmation (tape only): the last completed bar's stacked-imbalance
    /// contrast — falling back to its delta sign — must agree with the signal direction.</summary>
    private bool CheckFootprint(int signal)
    {
        if (_footprintBars.Count == 0) return false;
        var fp = _footprintBars[^1];
        var stack = fp.StackedBuy - fp.StackedSell;
        if (stack != 0) return signal > 0 ? stack > 0 : stack < 0;
        return signal > 0 ? fp.Delta > 0 : fp.Delta < 0;
    }

    private bool CheckMomentumAlignment(int signal)
    {
        if (BarDeltas.Count < 3) return false;
        for (var i = BarDeltas.Count - 3; i < BarDeltas.Count; i++)
        {
            if (signal > 0 && BarDeltas[i] <= 0) return false;
            if (signal < 0 && BarDeltas[i] >= 0) return false;
        }
        return true;
    }

    private bool CheckHtfEma(int signal)
    {
        if (!UseHtfFilter) return true;
        if (LastEmaHtf is not { } ema || LastBid is not { } bid || ema == 0) return false;
        return signal > 0 ? bid > ema : bid < ema;
    }

    private bool CheckEmaSlope(int signal)
    {
        var slopeBars = Math.Max(EmaSlopeBars, 1);
        if (_htfBars.Count < slopeBars + 50) return false;

        var emaNow    = Indicators.Ema(_htfBars, 50);
        var emaBefore = Indicators.Ema(_htfBars.Take(_htfBars.Count - slopeBars).ToList(), 50);
        if (double.IsNaN(emaNow) || double.IsNaN(emaBefore)) return false;

        var slope = emaNow > emaBefore ? 1 : (emaNow < emaBefore ? -1 : 0);
        return signal > 0 ? slope > 0 : (signal < 0 && slope < 0);
    }

    private bool CheckAdxTrending() =>
        LastAdx is { } adx && adx >= AdxThreshold;

    private bool CheckSpread()
    {
        if (SpreadBp() is not { } bp) return false;
        if (bp > MaxSpreadBp) return false;
        var avg = AvgSpread ?? 0;
        if (avg <= 0) return true; // bootstrap
        return LastSpread is { } cur && cur <= avg * SpreadAvgMultiplier;
    }

    /// <summary>Current spread in basis points of price (instrument-agnostic), or null pre-quote.</summary>
    private double? SpreadBp() =>
        LastSpread is { } s && LastBid is { } bid && bid > 0 ? s / bid * 10_000.0 : null;

    /// <summary>ATR(14) on the chart TF in basis points of price, or null while warming.</summary>
    private double? AtrBp() =>
        LastAtr is { } atr && LastBid is { } bid && bid > 0 ? atr / bid * 10_000.0 : null;

    // ---------- Pre-signal guards (cheap, run before confirmation count) ----------

    private bool CheckPreSignalGuards(out string reason)
    {
        if (UseSessionFilter && _currentSession == SessionId.None)
            { reason = "SESSION CLOSED"; return false; }

        if (SpreadBp() is { } sbp && sbp > MaxSpreadBp)
            { reason = $"SPREAD HIGH ({sbp:F2}bp>{MaxSpreadBp:F1}bp)"; return false; }

        if (TodaySignalCount >= MaxDailySignals)
            { reason = $"DAILY LIMIT ({TodaySignalCount}/{MaxDailySignals})"; return false; }

        if (_sessionSignalCount >= MaxSignalsPerSession)
            { reason = $"SESSION LIMIT ({_sessionSignalCount}/{MaxSignalsPerSession})"; return false; }

        var now = DateTime.UtcNow;
        if (_lastSignalTimeUtc != DateTime.MinValue
            && now < _lastSignalTimeUtc.AddSeconds(MinSecondsBetweenSignals))
        {
            var left = (_lastSignalTimeUtc.AddSeconds(MinSecondsBetweenSignals) - now).TotalSeconds;
            reason = $"COOLDOWN ({left:F0}s left)"; return false;
        }

        if (AtrBp() is { } abp)
        {
            if (abp < MinAtrBp) { reason = $"ATR LOW ({abp:F1}bp<{MinAtrBp:F1}bp)"; return false; }
            if (abp > MaxAtrBp) { reason = $"ATR HIGH ({abp:F1}bp>{MaxAtrBp:F1}bp)"; return false; }
        }
        else
        {
            reason = "ATR UNAVAILABLE"; return false;
        }

        reason = $"ACTIVE {_currentSession}";
        return true;
    }

    private int ClassifyCrossover(int cumDelta, int previousCumDelta)
    {
        var t = Math.Max(1, EffectiveThreshold);
        if (previousCumDelta <=  t && cumDelta >  t) return  1;
        if (previousCumDelta >= -t && cumDelta < -t) return -1;
        return 0;
    }

    // ---------- Gate / confirmation boards (rebuilt every closed bar for the UI) ----------

    private void UpdateGateBoard()
    {
        Gates.Clear();

        var sessionPass = !UseSessionFilter || _currentSession != SessionId.None;
        Gates.Add(new GateRow("Session", sessionPass,
            UseSessionFilter ? _currentSession.ToString() : "filter off"));

        var sbp = SpreadBp();
        Gates.Add(new GateRow("Spread cap", sbp is { } sv && sv <= MaxSpreadBp,
            sbp is { } sv2 ? $"{sv2:0.00} ≤ {MaxSpreadBp:0.0} bp" : "no quote"));

        var abp = AtrBp();
        Gates.Add(new GateRow("ATR band", abp is { } av && av >= MinAtrBp && av <= MaxAtrBp,
            abp is { } av2 ? $"{av2:0.0} in [{MinAtrBp:0.#}–{MaxAtrBp:0.#}] bp" : "warming"));

        var now = DateTime.UtcNow;
        var cooldownEnd = _lastSignalTimeUtc == DateTime.MinValue
            ? DateTime.MinValue : _lastSignalTimeUtc.AddSeconds(MinSecondsBetweenSignals);
        var cooldownPass = now >= cooldownEnd;
        Gates.Add(new GateRow("Cooldown", cooldownPass,
            cooldownPass ? "clear" : $"{(cooldownEnd - now).TotalSeconds:F0}s left"));

        Gates.Add(new GateRow("Caps", TodaySignalCount < MaxDailySignals && _sessionSignalCount < MaxSignalsPerSession,
            $"day {TodaySignalCount}/{MaxDailySignals} · sess {_sessionSignalCount}/{MaxSignalsPerSession}"));
    }

    private void UpdateConfirmationBoard(int lean)
    {
        Confirmations.Clear();
        var dir = lean == 0 ? 1 : lean;   // show the long-side evaluation when flat

        Confirmations.Add(new GateRow("Momentum 3-bar", CheckMomentumAlignment(dir), "bar Δs aligned"));
        Confirmations.Add(new GateRow("HTF EMA(50)", CheckHtfEma(dir),
            UseHtfFilter ? (LastEmaHtf is { } e ? $"bid vs {e:0.#####}" : "warming") : "filter off"));
        Confirmations.Add(new GateRow("EMA slope", CheckEmaSlope(dir), $"{EmaSlopeBars} × 15m bars"));
        Confirmations.Add(new GateRow("ADX trending", CheckAdxTrending(),
            LastAdx is { } adx ? $"{adx:0.0} ≥ {AdxThreshold:0.0}" : "warming"));
        Confirmations.Add(new GateRow("Spread calm", CheckSpread(),
            SpreadBp() is { } sb ? $"{sb:0.00} bp" : "no quote"));
        if (_sawRealTape)
        {
            var fp = _footprintBars.Count > 0 ? _footprintBars[^1] : null;
            Confirmations.Add(new GateRow("Footprint stack", CheckFootprint(dir),
                fp is null ? "no bar yet" : $"↑{fp.StackedBuy} ↓{fp.StackedSell} Δ{fp.Delta:+#;-#;0}"));
        }
    }

    // ---------- HTF (15m) bar stream: EMA(50) + ADX(14) ----------

    private async Task RunHtfBarsAsync(Contract contract, BrokerKind broker, CancellationToken ct)
    {
        var instrumentId = _services.Ingest.Resolve(contract, broker);
        try
        {
            var hist = await _services.Repository.GetHistoricalBarsAsync(contract, broker, BarSize.FifteenMinutes,
                TimeSpan.FromDays(2), ct);
            _htfBars.Clear();
            _htfBars.AddRange(hist);
            RecalculateHtfIndicators();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTF history fetch failed");
        }

        var channel = Channel.CreateBounded<Bar>(new BoundedChannelOptions(BarChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        using var subscription = _services.Hub.Bars(instrumentId, BarSize.FifteenMinutes).Subscribe(b =>
            channel.Writer.TryWrite(b.ToBar()));
        _htfBarsHandle = _services.Ingest.SubscribeBars(contract, broker, BarSize.FifteenMinutes);

        try
        {
            await foreach (var bar in channel.Reader.ReadAllAsync(ct))
            {
                await UiThread.RunAsync(() =>
                {
                    _htfBars.Add(bar);
                    if (_htfBars.Count > 400) _htfBars.RemoveAt(0);
                    RecalculateHtfIndicators();
                });
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTF bar stream ended");
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private void RecalculateHtfIndicators()
    {
        var ema = Indicators.Ema(_htfBars, 50);
        LastEmaHtf = double.IsNaN(ema) ? null : ema;

        var adx = Indicators.Adx(_htfBars, 14);
        LastAdx = double.IsNaN(adx) ? null : adx;
    }

    private void RecalculateAtr()
    {
        var atr = Indicators.Atr(Bars, 14);
        LastAtr = double.IsNaN(atr) ? null : atr;
    }

    // ---------- Sessions ----------

    private void UpdateSessionState()
    {
        var s = GetCurrentSession();
        if (s != _currentSession)
        {
            _currentSession = s;
            ActiveSession = s;
            _sessionSignalCount = 0;
            _logger.LogInformation("[CumulativeDelta] Session → {Session}", s);
        }
    }

    private SessionId GetCurrentSession()
    {
        var t = MinutesOfDayUtc(DateTime.UtcNow);
        if (OverlapOnly)
        {
            return InRange(t, OverlapStartHour, OverlapStartMin, OverlapEndHour, OverlapEndMin)
                ? SessionId.Overlap : SessionId.None;
        }
        if (UseAsiaSession    && InRange(t, AsiaStartHour,     0, AsiaEndHour,    0)) return SessionId.Asia;
        if (UseLondonSession  && InRange(t, LondonStartHour,   0, LondonEndHour,  0)) return SessionId.London;
        if (UseNewYorkSession && InRange(t, NewYorkStartHour, NewYorkStartMin, NewYorkEndHour, 0)) return SessionId.NewYork;
        return SessionId.None;
    }

    private static int MinutesOfDayUtc(DateTime utc) => utc.Hour * 60 + utc.Minute;

    private static bool InRange(int totalMinutes, int sH, int sM, int eH, int eM)
    {
        var start = sH * 60 + sM;
        var end   = eH * 60 + eM;
        return totalMinutes >= start && totalMinutes < end;
    }

    private void ResetDailyCountersIfNeeded()
    {
        var todayUtc = DateTime.UtcNow.Date;
        if (todayUtc != _lastSignalDayUtc)
        {
            _lastSignalDayUtc = todayUtc;
            TodaySignalCount = 0;
        }
    }

    // ---------- Helpers ----------

    private double AverageSpread()
    {
        if (_spreadHistoryFilled <= 0) return 0;
        var sum = 0.0;
        for (var i = 0; i < _spreadHistoryFilled; i++) sum += _spreadHistory[i];
        return sum / _spreadHistoryFilled;
    }

    private int SumWindow(int w)
    {
        var sum = 0;
        var n = Math.Min(_bufferFilled, w);
        for (var i = 0; i < n; i++) sum += _circularBuffer[i];
        return sum;
    }

    private static string DirectionLabel(int signal) => signal > 0 ? "LONG" : "SHORT";

    private void PushSignalLine(string line)
    {
        RecentSignals.Insert(0, line);
        while (RecentSignals.Count > 12) RecentSignals.RemoveAt(RecentSignals.Count - 1);
    }

    private void ResetState()
    {
        Array.Clear(_circularBuffer);
        Array.Clear(_spreadHistory);
        _bufferIndex = 0;
        _bufferFilled = 0;
        _previousCumDelta = 0;
        _spreadHistoryIdx = 0;
        _spreadHistoryFilled = 0;
        _prevBidInitialised = false;
        _uptickCount = 0;
        _downtickCount = 0;
        _sawRealTape = false;
        _priorTradePrice = 0;
        _priorTradeClass = AggressorSide.Unknown;
        _tapeBuyVolume = 0;
        _tapeSellVolume = 0;
        _barPrints.Clear();
        _cumulativeDeltaAll = 0;
        _footprintBars.Clear();
        _cumDeltaHistory.Clear();
        DeltaPoints.Clear();
        Gates.Clear();
        Confirmations.Clear();
        _sessionSignalCount = 0;
        _lastSignalTimeUtc = DateTime.MinValue;
        _currentSession = SessionId.None;
        ActiveSession = SessionId.None;
        LiveDelta = 0;
        CumulativeDelta = 0;
        AvgSpread = null;
        LastConfirmationScore = 0;
        FeedMode = "—";
        OnPropertyChanged(nameof(MaxConfirmations));
        FootprintChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>One pass/fail row in the gate or confirmation board.</summary>
public sealed record GateRow(string Name, bool Pass, string Detail);

/// <summary>One closed bar's delta sample for the time-axis delta pane.</summary>
public sealed record DeltaPoint(DateTime TimeUtc, double BarDelta, double WindowCum);

public enum SessionId
{
    None = 0,
    Asia = 1,
    London = 2,
    NewYork = 3,
    Overlap = 4,
}
