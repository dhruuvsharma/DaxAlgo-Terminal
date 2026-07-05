using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Ml;
using TradingTerminal.Core.Quant.TimeSeries;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;
using TradingTerminal.UI.Presets;

namespace TradingTerminal.OrderBook;

/// <summary>
/// Drives the standalone Order Book window. Mirrors <c>ChartsViewModel</c> in shape — it owns the
/// instrument picker universe and resolves the source broker — but instead of bars it subscribes to
/// <see cref="IMarketDataHub.Depth"/> and renders the full L2 ladder for the selected instrument.
///
/// <para>On top of the classic depth-of-market ladder it adds four analytics layers, all reusing the
/// pure helpers in <see cref="Microstructure"/> (Core):</para>
/// <list type="bullet">
/// <item><b>Microstructure strip</b> — microprice, weighted mid, L1 + cumulative queue imbalance,
/// book skew, sweep cost ("cost to fill" a chosen size on each side).</item>
/// <item><b>Liquidity heatmap</b> — a rolling ring buffer of <see cref="HeatColumn"/>s gives the book a
/// time axis: x = time, y = price, intensity = resting size, with best-bid/ask + microprice lines.</item>
/// <item><b>Imbalance / microprice trend</b> — an OLS slope (<see cref="Ols"/>) of cumulative imbalance
/// over the visible columns, drawn as a bottom lane on the heatmap and surfaced as a slope read-out.</item>
/// <item><b>Order flow</b> — opt-in trade tape (capability-gated, with a synthetic depth-mid fallback),
/// Lee-Ready aggressor classification, cumulative delta, and trade dots on the heatmap.</item>
/// </list>
///
/// <para>The streaming path is the canonical one: a single <see cref="IMarketDataIngest.Subscribe"/>
/// handle starts (or joins) the ref-counted broker L1/L2 pump, and this VM observes the hub's
/// <see cref="DepthSnapshot"/> stream keyed by <see cref="InstrumentId"/>. The heatmap is rendered in
/// code-behind off the <see cref="BookChanged"/> event — the same render-in-code-behind convention the
/// Volume Footprint / Order Flow Cube windows use. No view code lives here (MVVM rule).</para>
/// </summary>
public sealed partial class OrderBookViewModel : ViewModelBase, IDisposable
{
    /// <summary>Cap on how many instruments the picker shows at once (the broker universe can be
    /// thousands of symbols; the search box narrows it).</summary>
    public const int MaxInstrumentsDisplayed = 500;

    private const string LogSource = "Order Book";

    /// <summary>Top-N levels used for cumulative imbalance / weighted mid / book skew.</summary>
    private const int DepthLevelsForStats = 10;

    /// <summary>Default heatmap ring length (columns = the visible time window). At the
    /// <see cref="CaptureIntervalMs"/> cadence 600 columns ≈ 2.5 minutes; the options rail offers
    /// the lengths in <see cref="HeatWindowOptions"/>.</summary>
    private const int DefaultHeatColumns = 600;

    /// <summary>Capture/redraw cadence for the heatmap (ms). Decoupled from the depth update rate so a
    /// fast book can't pin the UI thread — each tick snapshots the latest book into one column.</summary>
    private const int CaptureIntervalMs = 250;

    /// <summary>Which brokers wire a native trade tape today (mirrors the footprint / cube VMs). The
    /// rest fall back to synthetic depth-mid-derived prints so the flow overlays aren't permanently empty.</summary>
    private static bool BrokerSupportsTradeTape(BrokerKind broker) => broker switch
    {
        BrokerKind.InteractiveBrokers => true,
        BrokerKind.Binance => true,
        BrokerKind.IronBeam => true,
        _ => false,
    };

    private readonly IMarketDataRepository _repository;
    private readonly IMarketDataHub _hub;
    private readonly IMarketDataIngest _ingest;
    private readonly IMarketDataStore _store;
    private readonly IModelRegistry _modelRegistry;
    private readonly IBrokerSelector _selector;
    private readonly InMemoryLogSink _log;
    private readonly ILogger<OrderBookViewModel> _logger;

    private IReadOnlyList<SignalInstrument> _allInstruments;
    private CancellationTokenSource? _streamCts;
    private IDisposable? _ingestHandle;
    private IDisposable? _tradeHandle;

    /// <summary>Latest whole-book snapshot, set by the depth drain and sampled by the capture timer.</summary>
    private DepthSnapshot? _latest;

    /// <summary>The snapshot last projected into the ladders/strip, so the capture tick can skip the
    /// (expensive) ladder reconcile when the book hasn't changed since the previous frame.</summary>
    private DepthSnapshot? _renderedSnapshot;

    /// <summary>Reused scratch buffers for the ladder projection so the per-frame rebuild allocates no
    /// temporary lists — the rows are reconciled into the bound collections in place (see <see cref="Reconcile"/>).</summary>
    private readonly List<OrderBookLevel> _bidScratch = new();
    private readonly List<OrderBookLevel> _askScratch = new();

    /// <summary>Trades that have printed since the last heatmap capture, drained into the next column.</summary>
    private readonly List<TradeMark> _pendingTrades = new();

    /// <summary>Lee-Ready carry state (prior trade price + prior classification) for inside-spread prints.</summary>
    private double _priorTradePrice;
    private AggressorSide _priorAggressor = AggressorSide.Unknown;
    private double? _priorMid; // synthetic-tape mid-tick detector

    private bool _useSyntheticTape;
    private readonly IDisposable _captureTimer;
    private bool _ready;

    // ── ML micro-forecaster ──────────────────────────────────────────────────────────────────
    /// <summary>Online order-book forecaster, stepped once per capture tick. Recreated per
    /// <see cref="Restart"/>; null while the warm-start backfill is still training it.</summary>
    private OrderBookMicroPredictor? _ml;

    /// <summary>Registry timeframe key for the order-book model — the fixed 250 ms capture cadence.</summary>
    private const string MlTimeframe = "250ms";

    /// <summary>The instrument key the current <see cref="_ml"/> is scoped to, captured when the model
    /// is built/restored so a later checkpoint saves it under the same registry coordinate.</summary>
    private string? _mlInstrumentKey;

    /// <summary>Last warm-start step boundary, so a live step can never re-learn the seam.</summary>
    private DateTime _mlWatermarkUtc = DateTime.MinValue;

    /// <summary>Signed trade volume / print count accumulated since the last capture tick — the
    /// flow inputs of the next <see cref="OrderBookStepSummary"/>.</summary>
    private long _flowSinceStep;
    private int _tradesSinceStep;

    /// <summary>How far back the warm-start replays stored depth, and its step-count cap
    /// (7 200 steps = 30 min at the capture cadence).</summary>
    private static readonly TimeSpan WarmStartLookback = TimeSpan.FromMinutes(30);
    private const int MaxWarmStartSteps = 7_200;

    public OrderBookViewModel(
        IMarketDataRepository repository,
        IMarketDataHub hub,
        IMarketDataIngest ingest,
        IMarketDataStore store,
        IModelRegistry modelRegistry,
        IBrokerSelector selector,
        InMemoryLogSink log,
        ILogger<OrderBookViewModel> logger)
    {
        _repository = repository;
        _hub = hub;
        _ingest = ingest;
        _store = store;
        _modelRegistry = modelRegistry;
        _selector = selector;
        _log = log;
        _logger = logger;

        _allInstruments = SignalInstrumentCatalog.All;
        Instruments = new ObservableCollection<SignalInstrument>(_allInstruments.Take(MaxInstrumentsDisplayed));
        SweepSizes = new ObservableCollection<int> { 100, 500, 1_000, 5_000, 10_000 };
        _selectedSweepSize = 1_000;
        HeatWindowOptions = new ObservableCollection<int> { 300, 600, 1_200, 2_400 };
        _heatWindowColumns = DefaultHeatColumns;
        PresetNames = new ObservableCollection<string>(_presetStore.Names);
        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                             ?? Instruments.FirstOrDefault();

        // Coalesced capture/render tick via the portable timer seam (WPF Dispatcher / Avalonia
        // Dispatcher under the hood). Returns an IDisposable owned by this VM.
        _captureTimer = UiThread.CreateRenderTimer(TimeSpan.FromMilliseconds(CaptureIntervalMs), OnCaptureTick);

        _ready = true;
        _ = LoadInstrumentsAsync();
        Restart();
    }

    public ObservableCollection<SignalInstrument> Instruments { get; }
    public ObservableCollection<int> SweepSizes { get; }

    /// <summary>Heatmap ring lengths the options rail offers (columns; one per capture tick).</summary>
    public ObservableCollection<int> HeatWindowOptions { get; }

    /// <summary>Saved preset names for the toolbar picker.</summary>
    public ObservableCollection<string> PresetNames { get; }

    /// <summary>Heatmap ring buffer (oldest first, left → right). Read by the code-behind renderer.</summary>
    public IReadOnlyList<HeatColumn> HeatColumns => _heatColumns;
    private readonly List<HeatColumn> _heatColumns = new();

    /// <summary>OLS fit of cumulative imbalance against column index over the visible columns
    /// (slope in imbalance-units per column, intercept at column 0). Drawn as the lane trend line and
    /// surfaced as <see cref="ImbalanceSlopeText"/>. Valid only when <see cref="HasImbalanceTrend"/>.</summary>
    public double ImbalanceSlope { get; private set; }
    public double ImbalanceIntercept { get; private set; }
    public bool HasImbalanceTrend { get; private set; }

    /// <summary>Ask side, displayed best-ask-at-the-bottom (highest price first) so the spread sits
    /// between the two ladders like a classic depth-of-market.</summary>
    [ObservableProperty] private ObservableCollection<OrderBookLevel> _asks = new();

    /// <summary>Bid side, best-bid first (descending price).</summary>
    [ObservableProperty] private ObservableCollection<OrderBookLevel> _bids = new();

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private string _status = "Pick an instrument to stream its order book.";
    [ObservableProperty] private double? _bestBid;
    [ObservableProperty] private double? _bestAsk;
    [ObservableProperty] private double? _spread;
    [ObservableProperty] private double? _mid;
    [ObservableProperty] private int _bidLevels;
    [ObservableProperty] private int _askLevels;
    [ObservableProperty] private DateTime? _lastUpdateUtc;

    // ── Tier 2: microstructure analytics strip ──────────────────────────────────────────────────
    [ObservableProperty] private double? _microprice;
    [ObservableProperty] private double? _weightedMid;
    /// <summary>L1 queue imbalance in [-1, 1] (top-of-book sizes only).</summary>
    [ObservableProperty] private double _imbalanceL1;
    /// <summary>Cumulative top-N queue imbalance in [-1, 1].</summary>
    [ObservableProperty] private double _imbalanceCum;
    [ObservableProperty] private string _imbalanceCumText = "—";
    /// <summary>Sign of cumulative imbalance: +1 bid-heavy, -1 ask-heavy, 0 balanced (drives colour).</summary>
    [ObservableProperty] private int _imbalanceDirection;
    /// <summary>Book skew = total bid depth ÷ total ask depth across the top-N (1 = balanced).</summary>
    [ObservableProperty] private string _bookSkewText = "—";
    /// <summary>Price cost to fill <see cref="SelectedSweepSize"/> sweeping the asks (a market buy).</summary>
    [ObservableProperty] private double? _sweepCostBuy;
    /// <summary>Price cost to fill <see cref="SelectedSweepSize"/> sweeping the bids (a market sell).</summary>
    [ObservableProperty] private double? _sweepCostSell;
    [ObservableProperty] private bool _sweepBuyShort;  // book too thin to fully fill the buy sweep
    [ObservableProperty] private bool _sweepSellShort;
    [ObservableProperty] private int _selectedSweepSize;

    // ── Tier 3: imbalance / microprice trend ────────────────────────────────────────────────────
    [ObservableProperty] private string _imbalanceSlopeText = "—";
    [ObservableProperty] private int _imbalanceSlopeDirection;

    // ── Tier 4: order flow ──────────────────────────────────────────────────────────────────────
    [ObservableProperty] private long _cumulativeDelta;
    [ObservableProperty] private int _cvdDirection;
    [ObservableProperty] private bool _tradeTapeLive;        // true when a native tape is wired
    [ObservableProperty] private string _tradeTapeText = "—";

    // ── Tier 5: ML micro-forecast (online-learned; see OrderBookMicroPredictor) ─────────────────
    /// <summary>The latest forecast for the renderer (violet path ahead of the heatmap) — null when
    /// the toggle is off, the model is warming up, or the book is unusable.</summary>
    public OrderBookForecast? MlForecast { get; private set; }

    [ObservableProperty] private bool _showMlForecast = true;

    /// <summary>Selectable learner algorithms for the direction (microprice) bank (Logistic is
    /// excluded — it fits the binary event heads). Switching retrains on the next restart.</summary>
    public IReadOnlyList<LearnerOption> Learners => Forecasters.DirectionChoices;

    [ObservableProperty] private LearnerOption _selectedLearner = Forecasters.DirectionChoices[0];
    /// <summary>Replay recent stored depth through the predictor on (re)start so it isn't cold.</summary>
    [ObservableProperty] private bool _warmStartFromHistory = true;
    [ObservableProperty] private string _mlSamplesText = "0";
    [ObservableProperty] private string _mlMaeText = "—";
    [ObservableProperty] private string _mlHitRateText = "—";
    [ObservableProperty] private string _baseMaeText = "—";
    [ObservableProperty] private string _baseHitRateText = "—";
    /// <summary>+1 when the model beats the queue-imbalance rule on rolling hit-rate, −1 when it
    /// loses, 0 while either lacks scores (drives the scoreboard colour).</summary>
    [ObservableProperty] private int _mlEdgeDirection;
    [ObservableProperty] private string _pSpreadText = "—";
    [ObservableProperty] private string _pDepthText = "—";
    [ObservableProperty] private string _pSweepText = "—";
    /// <summary>Raw probabilities in [0, 1] for the chip mini-bars.</summary>
    [ObservableProperty] private double _pSpread;
    [ObservableProperty] private double _pDepth;
    [ObservableProperty] private double _pSweep;
    /// <summary>Probability alert levels (0 calm &lt; 0.4 ≤ 1 elevated &lt; 0.7 ≤ 2 hot) for the chip colours.</summary>
    [ObservableProperty] private int _pSpreadLevel;
    [ObservableProperty] private int _pDepthLevel;
    [ObservableProperty] private int _pSweepLevel;

    // ── View toggles (presentation only — redraw, never restart the stream) ─────────────────────
    [ObservableProperty] private bool _showHeatmap = true;
    [ObservableProperty] private bool _showTrades = true;
    [ObservableProperty] private bool _showMicropriceLine = true;
    [ObservableProperty] private bool _showImbalanceLane = true;

    /// <summary>Freezes the capture/render loop (ladders, heatmap, ML steps) while the stream keeps
    /// flowing underneath — the freshest book is still tracked, so resume is instant. Lets the user
    /// actually read a fast book.</summary>
    [ObservableProperty] private bool _isPaused;

    /// <summary>Heatmap ring length in columns (one column per capture tick).</summary>
    [ObservableProperty] private int _heatWindowColumns;

    /// <summary>True once the current stream has projected at least one non-empty book — drives the
    /// heatmap empty-state overlay.</summary>
    [ObservableProperty] private bool _hasBook;

    // ── Presets (named view-option snapshots; see ToolPresetStore) ──────────────────────────────
    /// <summary>Editable preset-picker text: type a name and Save, or pick an existing preset to apply.</summary>
    [ObservableProperty] private string _presetName = string.Empty;
    [ObservableProperty] private string? _selectedPreset;

    /// <summary>Raised on the UI thread whenever the heatmap ring buffer changes and the canvas should
    /// redraw. The ladder + strip are plain bindings and update themselves.</summary>
    public event EventHandler? BookChanged;

    partial void OnInstrumentSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedInstrumentChanged(SignalInstrument? value) { if (_ready) Restart(); }
    partial void OnSelectedSweepSizeChanged(int value) { if (_latest is { } s) UpdateSweepCost(s); }

    partial void OnShowHeatmapChanged(bool value) => RaiseRedraw();
    partial void OnShowTradesChanged(bool value) => RaiseRedraw();
    partial void OnShowMicropriceLineChanged(bool value) => RaiseRedraw();
    partial void OnShowImbalanceLaneChanged(bool value) => RaiseRedraw();

    partial void OnShowMlForecastChanged(bool value)
    {
        PublishMlForecast();
        RaiseRedraw();
    }

    partial void OnSelectedLearnerChanged(LearnerOption value) { if (_ready) Restart(); }

    partial void OnIsPausedChanged(bool value) =>
        Status = value
            ? $"⏸ Paused — the stream keeps running in the background ({SelectedInstrument?.DisplayName})."
            : $"Resumed — {SelectedInstrument?.DisplayName}.";

    partial void OnHeatWindowColumnsChanged(int value)
    {
        if (value < 10) return;
        while (_heatColumns.Count > value) _heatColumns.RemoveAt(0);
        RaiseRedraw();
    }

    partial void OnSelectedPresetChanged(string? value)
    {
        if (value is null) return;
        PresetName = value;
        if (_presetStore.Get(value) is { } preset) ApplyPreset(preset);
    }

    private void RaiseRedraw()
    {
        if (_ready) BookChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Swaps the static fallback catalog for the connected broker's tradable universe,
    /// mapped to <see cref="SignalInstrument"/>. Keeps the static catalog on failure / empty list.</summary>
    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0) return;

            _allInstruments = list
                .Select(i => new SignalInstrument($"{i.DisplayName}  ·  {BrokerLabel(i.Broker)}", i.Category, i.Contract, i.Broker))
                .ToList();

            var keep = SelectedInstrument;
            ApplyFilter();
            SelectedInstrument = keep is not null && Instruments.Contains(keep)
                ? keep
                : _allInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY") ?? _allInstruments.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Order book: instrument list load failed; using static catalog");
        }
    }

    private void ApplyFilter()
    {
        var term = InstrumentSearchText?.Trim() ?? string.Empty;
        IEnumerable<SignalInstrument> query = _allInstruments;
        if (term.Length > 0)
            query = _allInstruments.Where(i => i.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase));

        var shown = query.Take(MaxInstrumentsDisplayed).ToList();
        var keep = SelectedInstrument;
        if (keep is not null && !shown.Contains(keep)) shown.Insert(0, keep);

        Instruments.Clear();
        foreach (var inst in shown) Instruments.Add(inst);
    }

    private void Restart()
    {
        StopStream();
        SaveModelCheckpoint();
        ResetState();
        var instrument = SelectedInstrument;
        if (instrument is null) return;

        BrokerKind broker;
        try { broker = ResolveBroker(instrument); }
        catch (InvalidOperationException ex) { Status = ex.Message; ClearBook(); return; }

        _useSyntheticTape = !BrokerSupportsTradeTape(broker);
        TradeTapeLive = !_useSyntheticTape;
        TradeTapeText = _useSyntheticTape ? "synthetic (depth-mid)" : "live tape";

        try
        {
            var id = _ingest.Resolve(instrument.Contract, broker);
            _streamCts = new CancellationTokenSource();
            // Single ref-counted handle powers both L1 and depth on the ingest side.
            _ingestHandle = _ingest.Subscribe(instrument.Contract, broker);
            if (!_useSyntheticTape)
                _tradeHandle = _ingest.SubscribeTrades(instrument.Contract, broker);

            Status = $"Streaming order book — {instrument.DisplayName} ({BrokerLabel(broker)})…";
            _log.Append(LogSource, "INFO",
                $"Order book on {instrument.DisplayName} [{BrokerLabel(broker)}] — depth + {TradeTapeText}");

            _ = RunDepthStreamAsync(id, _streamCts.Token);
            if (!_useSyntheticTape)
                _ = RunTradeStreamAsync(id, _streamCts.Token);
            _ = InitializeMlAsync(id, _streamCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Order book: subscribe failed for {Symbol}", instrument.Contract.Symbol);
            Status = $"Subscribe failed: {ex.Message}";
        }
    }

    /// <summary>Pumps depth snapshots off the hub through a bounded channel (so the publish thread is
    /// never blocked by the UI) and stores the freshest one for the capture timer to project.</summary>
    private const int DepthChannelCapacity = 2_048;

    private async Task RunDepthStreamAsync(InstrumentId instrumentId, CancellationToken ct)
    {
        // Bounded + DropOldest so a fast book the UI can't keep up with is capped in memory (newest
        // snapshot wins) instead of piling an unbounded backlog into GBs.
        var channel = Channel.CreateBounded<DepthSnapshot>(new BoundedChannelOptions(DepthChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        using var subscription = _hub.Depth(instrumentId).Subscribe(s => channel.Writer.TryWrite(s));

        try
        {
            while (await channel.Reader.WaitToReadAsync(ct))
            {
                DepthSnapshot? latest = null;
                while (channel.Reader.TryRead(out var s)) latest = s;
                // Store only (atomic reference write) — the capture tick projects the freshest book
                // into the ladders/strip/heatmap at a fixed cadence. This decouples the (expensive)
                // ladder rebuild from the depth-update rate: a fast book can no longer pin the UI
                // thread regenerating every ItemsControl row on each tick (the old lag source).
                if (latest is { } snap) _latest = snap;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Order book: depth stream ended");
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private const int TradeChannelCapacity = 100_000;
    private const int MaxDrainBatch = 8_192;

    /// <summary>Pumps the native trade tape: classifies each print (Lee-Ready when the broker doesn't
    /// report a side), folds it into the cumulative delta, and queues a <see cref="TradeMark"/> for the
    /// next heatmap column. Bounded + DropOldest mirrors the footprint VM's anti-pile-up backstop.</summary>
    private async Task RunTradeStreamAsync(InstrumentId instrumentId, CancellationToken ct)
    {
        var channel = Channel.CreateBounded<TradePrint>(new BoundedChannelOptions(TradeChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        using var sub = _hub.Trades(instrumentId).Subscribe(t => channel.Writer.TryWrite(t));

        var batch = new List<TradePrint>(MaxDrainBatch);
        try
        {
            while (await channel.Reader.WaitToReadAsync(ct))
            {
                batch.Clear();
                while (batch.Count < MaxDrainBatch && channel.Reader.TryRead(out var t)) batch.Add(t);
                if (batch.Count == 0) continue;
                await UiThread.RunAsync(() => IngestTrades(batch));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Order book: trade stream ended");
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    /// <summary>Classifies a batch of real prints and accumulates them (UI thread). The heavy redraw is
    /// left to the capture tick; this only updates the running delta + the pending-trade buffer.</summary>
    private void IngestTrades(List<TradePrint> batch)
    {
        var bid = _latest?.BestBid ?? 0;
        var ask = _latest?.BestAsk ?? 0;
        long delta = 0;
        foreach (var t in batch)
        {
            var side = t.Aggressor != AggressorSide.Unknown
                ? t.Aggressor
                : Microstructure.ClassifyAggressor(t.Price, bid, ask, _priorTradePrice, _priorAggressor);
            if (side != AggressorSide.Unknown) _priorAggressor = side;
            _priorTradePrice = t.Price;

            if (side == AggressorSide.Buy) delta += t.Size;
            else if (side == AggressorSide.Sell) delta -= t.Size;

            _pendingTrades.Add(new TradeMark(t.Price, t.Size, side));
        }
        CumulativeDelta += delta;
        CvdDirection = Math.Sign(CumulativeDelta);
        _flowSinceStep += delta;
        _tradesSinceStep += batch.Count;
    }

    /// <summary>Projects the freshest whole-book snapshot into the two display ladders and recomputes
    /// the microstructure strip. Called from the capture tick (UI thread), not per depth update, so the
    /// ladder rebuild is coalesced. Rows are reconciled into the bound collections <b>in place</b> so
    /// WPF only re-templates the rows that actually changed instead of every row each frame.</summary>
    private void RenderLatestBook(DepthSnapshot snapshot)
    {
        long maxSize = 1;
        foreach (var l in snapshot.Bids) if (l.Size > maxSize) maxSize = l.Size;
        foreach (var l in snapshot.Asks) if (l.Size > maxSize) maxSize = l.Size;

        // Bids: best-first (already descending). Cumulative from the top of book down.
        _bidScratch.Clear();
        long cum = 0;
        foreach (var l in snapshot.Bids)
        {
            cum += l.Size;
            _bidScratch.Add(new OrderBookLevel(l.Price, l.Size, cum, (double)l.Size / maxSize));
        }
        Reconcile(Bids, _bidScratch);

        // Asks: cumulative is computed best-first (ascending), but displayed highest-price-first so
        // the best ask sits just above the spread. Reverse after accumulating.
        _askScratch.Clear();
        cum = 0;
        foreach (var l in snapshot.Asks)
        {
            cum += l.Size;
            _askScratch.Add(new OrderBookLevel(l.Price, l.Size, cum, (double)l.Size / maxSize));
        }
        _askScratch.Reverse();
        Reconcile(Asks, _askScratch);

        BidLevels = snapshot.Bids.Count;
        AskLevels = snapshot.Asks.Count;
        BestBid = snapshot.BestBid > 0 ? snapshot.BestBid : null;
        BestAsk = snapshot.BestAsk > 0 ? snapshot.BestAsk : null;
        Spread = BestBid is { } b && BestAsk is { } a ? a - b : null;
        Mid = BestBid is { } bb && BestAsk is { } aa ? (aa + bb) * 0.5 : null;
        LastUpdateUtc = snapshot.TimestampUtc;
        HasBook = true;

        UpdateAnalytics(snapshot);

        Status = $"{SelectedInstrument?.DisplayName} · {snapshot.Bids.Count} bid / {snapshot.Asks.Count} ask levels · {snapshot.TimestampUtc:HH:mm:ss}";
    }

    /// <summary>Mutates <paramref name="target"/> to equal <paramref name="source"/> with the fewest
    /// collection-change notifications: overwrite differing rows by index (a Replace updates one item
    /// container), append the surplus, trim the tail. Avoids tearing down and rebuilding every ladder
    /// row's visual tree each frame, which is what made a fast book lag the window.</summary>
    private static void Reconcile(ObservableCollection<OrderBookLevel> target, List<OrderBookLevel> source)
    {
        for (var i = 0; i < source.Count; i++)
        {
            if (i < target.Count)
            {
                if (!target[i].Equals(source[i])) target[i] = source[i];
            }
            else target.Add(source[i]);
        }
        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
    }

    /// <summary>Tier 2 strip: microprice / weighted mid / imbalance / skew, all from <see cref="Microstructure"/>.</summary>
    private void UpdateAnalytics(DepthSnapshot snapshot)
    {
        if (snapshot.Bids.Count == 0 || snapshot.Asks.Count == 0)
        {
            Microprice = WeightedMid = null;
            ImbalanceL1 = ImbalanceCum = 0;
            ImbalanceCumText = "—"; ImbalanceDirection = 0;
            BookSkewText = "—";
            SweepCostBuy = SweepCostSell = null;
            return;
        }

        var topBidSize = snapshot.Bids[0].Size;
        var topAskSize = snapshot.Asks[0].Size;
        Microprice = Microstructure.Microprice(snapshot.BestBid, snapshot.BestAsk, topBidSize, topAskSize);
        WeightedMid = Microstructure.WeightedMidPrice(snapshot, DepthLevelsForStats);
        ImbalanceL1 = Microstructure.QueueImbalance(topBidSize, topAskSize);
        ImbalanceCum = Microstructure.CumulativeImbalance(snapshot, DepthLevelsForStats);
        ImbalanceCumText = ImbalanceCum.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        ImbalanceDirection = ImbalanceCum > 0.02 ? 1 : ImbalanceCum < -0.02 ? -1 : 0;

        var bidDepth = Microstructure.SideDepth(snapshot.Bids, DepthLevelsForStats);
        var askDepth = Microstructure.SideDepth(snapshot.Asks, DepthLevelsForStats);
        BookSkewText = askDepth > 0
            ? ((double)bidDepth / askDepth).ToString("0.00×", CultureInfo.InvariantCulture)
            : "—";

        UpdateSweepCost(snapshot);
    }

    private void UpdateSweepCost(DepthSnapshot snapshot)
    {
        if (snapshot.Asks.Count == 0 || snapshot.Bids.Count == 0) { SweepCostBuy = SweepCostSell = null; return; }
        SweepCostBuy = Microstructure.EstimatedSlippage(snapshot.Asks, SelectedSweepSize, out var buyFull);
        SweepCostSell = Microstructure.EstimatedSlippage(snapshot.Bids, SelectedSweepSize, out var sellFull);
        SweepBuyShort = !buyFull;
        SweepSellShort = !sellFull;
    }

    /// <summary>Capture tick (~4 fps): snapshots the latest book into one heatmap column, attaches the
    /// trades that printed since the last tick (real or synthesized), recomputes the imbalance trend,
    /// trims the ring, and asks the canvas to redraw. Decoupled from the depth rate by design.</summary>
    private void OnCaptureTick()
    {
        if (!_ready || IsPaused) return;
        var snap = _latest;
        if (snap is null || snap.Bids.Count == 0 || snap.Asks.Count == 0) return;

        // Project the freshest book into the ladders + strip — coalesced to this cadence so the ladder
        // rebuild is decoupled from the depth-update rate. Skip when the book is unchanged since the
        // last frame (the heatmap column below still scrolls the time axis either way).
        if (!ReferenceEquals(snap, _renderedSnapshot))
        {
            RenderLatestBook(snap);
            _renderedSnapshot = snap;
        }

        // Synthetic tape: emit a mid-tick-derived print when no native tape is wired, so the flow
        // overlays still show something against the fake client / cTrader / Alpaca.
        if (_useSyntheticTape) SynthesizeTrade(snap);

        List<TradeMark>? trades = null;
        if (_pendingTrades.Count > 0)
        {
            trades = new List<TradeMark>(_pendingTrades);
            _pendingTrades.Clear();
        }

        var column = new HeatColumn
        {
            TimeUtc = snap.TimestampUtc == default ? DateTime.UtcNow : snap.TimestampUtc,
            Bids = snap.Bids,
            Asks = snap.Asks,
            BestBid = snap.BestBid,
            BestAsk = snap.BestAsk,
            Microprice = Microprice ?? (snap.BestBid + snap.BestAsk) * 0.5,
            Imbalance = ImbalanceCum,
            Trades = trades,
        };
        _heatColumns.Add(column);
        while (_heatColumns.Count > Math.Max(10, HeatWindowColumns)) _heatColumns.RemoveAt(0);

        // ML step: one summary per capture tick (even when the book is unchanged — the targets
        // are time-based). The step time is the tick's wall clock, not the snapshot's event time,
        // so a quiet book still advances the model's horizon bookkeeping. Sub-millisecond work.
        if (_ml is { } ml)
        {
            var stepTime = DateTime.UtcNow;
            if (stepTime > _mlWatermarkUtc)
            {
                var step = OrderBookStepSummary.From(snap, DepthLevelsForStats, SelectedSweepSize,
                    _flowSinceStep, _tradesSinceStep, tradeFlowValid: true, timestampUtc: stepTime);
                ml.OnStep(step);
                PublishMlForecast();
            }
        }
        _flowSinceStep = 0;
        _tradesSinceStep = 0;

        UpdateImbalanceTrend();
        BookChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>L1 tick-rule synthesizer over the depth mid: mid ticks up ⇒ buy print at the ask
    /// (size = ask size), down ⇒ sell at the bid. Keeps the flow overlays alive on tape-less brokers.</summary>
    private void SynthesizeTrade(DepthSnapshot snap)
    {
        var mid = (snap.BestBid + snap.BestAsk) * 0.5;
        if (_priorMid is { } prev && mid != prev)
        {
            var isBuy = mid > prev;
            var price = isBuy ? snap.BestAsk : snap.BestBid;
            var size = Math.Max(1L, isBuy ? snap.Asks[0].Size : snap.Bids[0].Size);
            var side = isBuy ? AggressorSide.Buy : AggressorSide.Sell;
            _pendingTrades.Add(new TradeMark(price, size, side));
            CumulativeDelta += isBuy ? size : -size;
            CvdDirection = Math.Sign(CumulativeDelta);
            _flowSinceStep += isBuy ? size : -size;
            _tradesSinceStep++;
        }
        _priorMid = mid;
    }

    /// <summary>Creates and publishes the ML forecaster for the (re)started stream. When warm-start
    /// is on, first trains it through recent stored depth on a thread-pool thread — against purely
    /// local state — and only then assigns <see cref="_ml"/>, so live steps can never race the
    /// backfill. An empty store or a store error degrades to a cold start.</summary>
    private async Task InitializeMlAsync(InstrumentId instrumentId, CancellationToken ct)
    {
        var learner = SelectedLearner.Kind;
        var ml = new OrderBookMicroPredictor(new OrderBookPredictorOptions(Learner: learner));
        var instrumentKey = instrumentId.ToString();
        var restored = TryRestoreModel(ml, instrumentKey, learner);
        if (!restored && WarmStartFromHistory)
        {
            try
            {
                var trained = await WarmStartAsync(instrumentId, ml, ct);
                if (trained > 0)
                    _log.Append(LogSource, "INFO",
                        $"ML warm-start: trained on {trained} stored depth steps ({(ml.IsReady ? "ready" : "still cold")})");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Order book: ML warm-start failed; starting cold");
            }
        }
        if (ct.IsCancellationRequested) return;
        _ml = ml;
        _mlInstrumentKey = instrumentKey;
        PublishMlForecast();
    }

    /// <summary>Loads the newest saved checkpoint for this instrument (250 ms cadence) and restores
    /// it into <paramref name="ml"/>. Returns false (cold) when none is stored or it is incompatible,
    /// so the caller falls back to the stored-depth warm-start.</summary>
    private bool TryRestoreModel(OrderBookMicroPredictor ml, string instrumentKey, LearnerKind learner)
    {
        try
        {
            var key = new ModelKey(
                OrderBookMicroPredictor.ModelKind, instrumentKey, MlTimeframe, Forecasters.Tag(learner));
            var saved = _modelRegistry.LoadLatest(key);
            if (saved is null || !ml.TryRestore(saved)) return false;
            _log.Append(LogSource, "INFO",
                $"ML: restored saved model ({saved.SamplesTrained:N0} steps trained, MAE {saved.Metrics.MlMaeTicks:F2}t) — skipping cold warm-start");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Order book: ML restore failed; starting fresh");
            return false;
        }
    }

    /// <summary>Checkpoints the current model into the registry (as a new version) when it is ready
    /// and scoped to a known instrument — called on restart and window close so the next session
    /// resumes warm. Best-effort; a store failure is logged, never thrown.</summary>
    private void SaveModelCheckpoint()
    {
        var ml = _ml;
        if (ml is null || !ml.IsReady || _mlInstrumentKey is null) return;
        try
        {
            var stored = _modelRegistry.Save(ml.CreateArtifact(_mlInstrumentKey, MlTimeframe));
            _log.Append(LogSource, "INFO",
                $"ML: saved model for {_mlInstrumentKey} {MlTimeframe} (v{stored.Version}, {ml.SamplesSeen:N0} steps)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Order book: ML checkpoint save failed");
        }
    }

    /// <summary>Replays up to <see cref="WarmStartLookback"/> of stored depth (all brokers merged —
    /// <c>ReadDepthAsync</c> has no source filter) through the same step shape the live tick builds,
    /// resampled onto the capture grid by <see cref="DepthStepSampler"/>. Returns steps trained.</summary>
    private async Task<int> WarmStartAsync(InstrumentId instrumentId, OrderBookMicroPredictor ml, CancellationToken ct)
    {
        var to = DateTime.UtcNow;
        var from = to - WarmStartLookback;
        var sweepSize = SelectedSweepSize;
        var trained = 0;
        var lastBoundary = DateTime.MinValue;

        await Task.Run(async () =>
        {
            var sampler = new DepthStepSampler(TimeSpan.FromMilliseconds(CaptureIntervalMs), DepthLevelsForStats, sweepSize);
            var steps = new List<OrderBookStepSummary>(32);
            await foreach (var snapshot in _store.ReadDepthAsync(instrumentId, from, to, ct))
            {
                steps.Clear();
                sampler.Add(snapshot, steps);
                foreach (var step in steps)
                {
                    ml.OnStep(step);
                    if (++trained >= MaxWarmStartSteps) break;
                }
                if (trained >= MaxWarmStartSteps) break;
            }
            lastBoundary = sampler.LastBoundaryUtc;
        }, ct);

        _mlWatermarkUtc = lastBoundary;
        return trained;
    }

    /// <summary>Republishes the renderer's forecast + the probability chips + the scoreboard from
    /// the engine's latest state. Cheap; called per step and from the toggle handler.</summary>
    private void PublishMlForecast()
    {
        var forecast = _ml?.LastForecast;
        MlForecast = ShowMlForecast ? forecast : null;

        if (forecast is not null)
        {
            (PSpreadText, PSpreadLevel) = FormatProbability(forecast.PSpreadWidens);
            (PDepthText, PDepthLevel) = FormatProbability(forecast.PDepthDrains);
            (PSweepText, PSweepLevel) = FormatProbability(forecast.PSweepJumps);
            PSpread = forecast.PSpreadWidens;
            PDepth = forecast.PDepthDrains;
            PSweep = forecast.PSweepJumps;
        }
        else
        {
            PSpreadText = PDepthText = PSweepText = "—";
            PSpreadLevel = PDepthLevel = PSweepLevel = 0;
            PSpread = PDepth = PSweep = 0;
        }
        UpdateMlStats();
    }

    private void UpdateMlStats()
    {
        if (_ml is null)
        {
            MlSamplesText = "0";
            MlMaeText = MlHitRateText = BaseMaeText = BaseHitRateText = "—";
            MlEdgeDirection = 0;
            return;
        }

        MlSamplesText = _ml.SamplesSeen.ToString("N0", CultureInfo.InvariantCulture);
        var mlAccuracy = _ml.MlAccuracy;
        var baseAccuracy = _ml.BaselineAccuracy;
        (MlMaeText, MlHitRateText) = FormatAccuracy(mlAccuracy);
        (BaseMaeText, BaseHitRateText) = FormatAccuracy(baseAccuracy);
        MlEdgeDirection = mlAccuracy.ScoredCount >= 20 && baseAccuracy.ScoredCount >= 20
            ? Math.Sign(mlAccuracy.DirectionalHitRate - baseAccuracy.DirectionalHitRate)
            : 0;
    }

    private static (string text, int level) FormatProbability(double p) =>
        ((p * 100).ToString("0", CultureInfo.InvariantCulture) + "%", p >= 0.7 ? 2 : p >= 0.4 ? 1 : 0);

    private static (string mae, string hit) FormatAccuracy(ForecastAccuracy accuracy) =>
        accuracy.ScoredCount == 0
            ? ("—", "—")
            : (accuracy.PocMaeTicks.ToString("0.00", CultureInfo.InvariantCulture) + " t",
               (accuracy.DirectionalHitRate * 100).ToString("0", CultureInfo.InvariantCulture) + "%");

    /// <summary>Tier 3: OLS fit of cumulative imbalance vs column index over the ring buffer. Slope is
    /// the directional pressure trend (imbalance-units per column); feeds the lane line + read-out.</summary>
    private void UpdateImbalanceTrend()
    {
        var n = _heatColumns.Count;
        if (n < 4)
        {
            HasImbalanceTrend = false;
            ImbalanceSlope = ImbalanceIntercept = 0;
            ImbalanceSlopeText = "—"; ImbalanceSlopeDirection = 0;
            return;
        }

        var x = new double[n][];
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = new[] { 1.0, i };       // intercept + column index
            y[i] = _heatColumns[i].Imbalance;
        }

        var fit = Ols.Fit(x, y);
        if (fit is null)
        {
            HasImbalanceTrend = false;
            ImbalanceSlopeText = "—"; ImbalanceSlopeDirection = 0;
            return;
        }

        ImbalanceIntercept = fit.Beta[0];
        ImbalanceSlope = fit.Beta[1];
        HasImbalanceTrend = true;
        ImbalanceSlopeText = $"{ImbalanceSlope * 100:+0.00;-0.00;0.00}/col";
        ImbalanceSlopeDirection = ImbalanceSlope > 1e-5 ? 1 : ImbalanceSlope < -1e-5 ? -1 : 0;
    }

    private void ResetState()
    {
        _latest = null;
        _renderedSnapshot = null;
        HasBook = false;
        Bids.Clear();
        Asks.Clear();
        _pendingTrades.Clear();
        _heatColumns.Clear();
        _priorTradePrice = 0;
        _priorAggressor = AggressorSide.Unknown;
        _priorMid = null;
        CumulativeDelta = 0;
        CvdDirection = 0;
        HasImbalanceTrend = false;
        ImbalanceSlope = ImbalanceIntercept = 0;
        ImbalanceSlopeText = "—"; ImbalanceSlopeDirection = 0;
        _ml = null;
        _mlInstrumentKey = null;
        _mlWatermarkUtc = DateTime.MinValue;
        _flowSinceStep = 0;
        _tradesSinceStep = 0;
        MlForecast = null;
        MlSamplesText = "0";
        MlMaeText = MlHitRateText = BaseMaeText = BaseHitRateText = "—";
        MlEdgeDirection = 0;
        PSpreadText = PDepthText = PSweepText = "—";
        PSpreadLevel = PDepthLevel = PSweepLevel = 0;
        PSpread = PDepth = PSweep = 0;
        BookChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearBook()
    {
        _renderedSnapshot = null;
        HasBook = false;
        Bids.Clear();
        Asks.Clear();
        BidLevels = AskLevels = 0;
        BestBid = BestAsk = Spread = Mid = null;
        Microprice = WeightedMid = null;
        ImbalanceL1 = ImbalanceCum = 0;
        ImbalanceCumText = "—"; ImbalanceDirection = 0;
        BookSkewText = "—";
        SweepCostBuy = SweepCostSell = null;
        LastUpdateUtc = null;
        _heatColumns.Clear();
        BookChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Presets ──────────────────────────────────────────────────────────────────────────────────

    private readonly ToolPresetStore<OrderBookPreset> _presetStore = new("order-book");

    [RelayCommand]
    private void SavePreset()
    {
        var name = PresetName.Trim();
        if (name.Length == 0) return;
        _presetStore.Save(name, new OrderBookPreset(
            ShowHeatmap, ShowTrades, ShowMicropriceLine, ShowImbalanceLane,
            ShowMlForecast, WarmStartFromHistory, SelectedSweepSize, HeatWindowColumns));
        RefreshPresetNames(selected: name);
        _log.Append(LogSource, "INFO", $"Preset '{name}' saved");
    }

    [RelayCommand]
    private void DeletePreset()
    {
        var name = SelectedPreset ?? PresetName.Trim();
        if (string.IsNullOrEmpty(name) || !_presetStore.Delete(name)) return;
        RefreshPresetNames(selected: null);
        _log.Append(LogSource, "INFO", $"Preset '{name}' deleted");
    }

    private void ApplyPreset(OrderBookPreset preset)
    {
        ShowHeatmap = preset.ShowHeatmap;
        ShowTrades = preset.ShowTrades;
        ShowMicropriceLine = preset.ShowMicropriceLine;
        ShowImbalanceLane = preset.ShowImbalanceLane;
        ShowMlForecast = preset.ShowMlForecast;
        WarmStartFromHistory = preset.WarmStartFromHistory;
        if (SweepSizes.Contains(preset.SweepSize)) SelectedSweepSize = preset.SweepSize;
        if (HeatWindowOptions.Contains(preset.HeatWindowColumns)) HeatWindowColumns = preset.HeatWindowColumns;
    }

    private void RefreshPresetNames(string? selected)
    {
        PresetNames.Clear();
        foreach (var n in _presetStore.Names) PresetNames.Add(n);
        SelectedPreset = selected;
    }

    // ── CSV export (VM-side via the portable UiFile seam; PNG snapshots stay view-side) ─────────

    [RelayCommand]
    private async Task ExportLadderCsvAsync()
    {
        if (Bids.Count == 0 && Asks.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("side,price,size,cumulative");
        for (var i = Asks.Count - 1; i >= 0; i--) AppendLevel(sb, "ask", Asks[i]);   // best ask first
        foreach (var level in Bids) AppendLevel(sb, "bid", level);                   // best bid first
        await SaveCsvAsync($"orderbook-ladder-{SymbolToken()}", sb.ToString());

        static void AppendLevel(StringBuilder sb, string side, OrderBookLevel level) =>
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{side},{level.Price},{level.Size},{level.Cumulative}"));
    }

    [RelayCommand]
    private async Task ExportSeriesCsvAsync()
    {
        if (_heatColumns.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("time_utc,best_bid,best_ask,microprice,imbalance,trade_count,signed_trade_volume");
        foreach (var c in _heatColumns)
        {
            long signedVolume = 0;
            var tradeCount = 0;
            if (c.Trades is { } trades)
            {
                tradeCount = trades.Count;
                foreach (var t in trades) signedVolume += t.Side == AggressorSide.Sell ? -t.Size : t.Size;
            }
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{c.TimeUtc:O},{c.BestBid},{c.BestAsk},{c.Microprice},{c.Imbalance},{tradeCount},{signedVolume}"));
        }
        await SaveCsvAsync($"orderbook-series-{SymbolToken()}", sb.ToString());
    }

    private string SymbolToken() =>
        (SelectedInstrument?.Contract.Symbol ?? "book").Replace('/', '-').Replace(':', '-');

    private async Task SaveCsvAsync(string baseName, string content)
    {
        try
        {
            var path = await UiFile.SaveAsync("CSV", new[] { "csv" },
                $"{baseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
            if (path is null) return;
            await File.WriteAllTextAsync(path, content);
            _log.Append(LogSource, "INFO", $"Exported → {path}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Order book: CSV export failed");
            Status = $"Export failed: {ex.Message}";
        }
    }

    private BrokerKind ResolveBroker(SignalInstrument instrument)
    {
        if (instrument.Broker is { } explicitBroker && _selector.IsConnected(explicitBroker))
            return explicitBroker;
        var connected = _selector.Connected;
        if (connected.Count == 0)
            throw new InvalidOperationException("No broker is connected. Connect at least one broker in the login screen.");
        return connected[0];
    }

    private static string BrokerLabel(BrokerKind broker) => broker switch
    {
        BrokerKind.InteractiveBrokers => "IB",
        BrokerKind.NinjaTrader => "NinjaTrader",
        BrokerKind.CTrader => "cTrader",
        BrokerKind.Alpaca => "Alpaca",
        _ => broker.ToString(),
    };

    private void StopStream()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        _ingestHandle?.Dispose();
        _ingestHandle = null;
        _tradeHandle?.Dispose();
        _tradeHandle = null;
    }

    public void Dispose()
    {
        _captureTimer.Dispose();
        SaveModelCheckpoint();
        StopStream();
    }
}

/// <summary>A named snapshot of the Order Book window's view options, persisted per user by
/// <see cref="ToolPresetStore{T}"/> (LocalAppData\DaxAlgo Terminal\tool-presets\order-book.json).</summary>
public sealed record OrderBookPreset(
    bool ShowHeatmap,
    bool ShowTrades,
    bool ShowMicropriceLine,
    bool ShowImbalanceLane,
    bool ShowMlForecast,
    bool WarmStartFromHistory,
    int SweepSize,
    int HeatWindowColumns);
