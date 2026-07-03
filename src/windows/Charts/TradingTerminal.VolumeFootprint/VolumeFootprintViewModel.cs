using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Ml;
using TradingTerminal.Core.Quant;
using TradingTerminal.UI;
using TradingTerminal.UI.Logging;

namespace TradingTerminal.VolumeFootprint;

/// <summary>
/// Drives the Volume Footprint window — a bid/ask cluster chart. Mirrors the order-flow strategy VMs:
/// it resolves the source broker, pumps the trade tape (with a synthetic L1-derived fallback for
/// brokers that don't wire <c>SubscribeTradesAsync</c>, so the chart still works against the fake
/// client / cTrader / Alpaca), and aggregates each <see cref="TradePrint"/> into time-bucketed
/// <see cref="RenderBar"/>s via <see cref="FootprintFeatures.BuildBar"/> (the shared Core extractor).
///
/// The window renders the cluster grid onto a Canvas in code-behind off the <see cref="FootprintChanged"/>
/// event — same convention the Order Flow Cube window uses (presentation, not business logic). This VM
/// holds no view code.
/// </summary>
public sealed partial class VolumeFootprintViewModel : ViewModelBase, IDisposable
{
    public const int MaxInstrumentsDisplayed = 500;
    private const string LogSource = "Volume Footprint";

    /// <summary>Which brokers actually wire a native trade tape today (see the cube VM's note). The
    /// rest fall back to synthetic L1-derived prints so the chart isn't permanently empty.</summary>
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
    private readonly IBrokerSelector _selector;
    private readonly InMemoryLogSink _log;
    private readonly ILogger<VolumeFootprintViewModel> _logger;

    private IReadOnlyList<SignalInstrument> _allInstruments;
    private CancellationTokenSource? _streamCts;
    private IDisposable? _quoteHandle;
    private IDisposable? _tradeHandle;
    private readonly QuoteTradeSynthesizer _synth = new();
    private bool _useSynthetic;

    // ── Per-bar accumulator ────────────────────────────────────────────────────────────────
    // The bucketer keeps the raw prints for the forming bar and calls FootprintFeatures.BuildBar
    // (Core) to seal buckets / rebuild the forming bar once per render tick. The same bucketer
    // implementation drives the ML warm-start backfill, so historical training bars are built
    // through the exact same path as live ones.
    private FootprintTimeBucketer? _bucketer;

    /// <summary>Online next-bar forecaster. Recreated on every <see cref="Restart"/> (it is
    /// instrument/interval/tick scoped); null while the warm-start backfill is still training it.</summary>
    private FootprintNextBarPredictor? _ml;

    /// <summary>Start time of the last bar the warm-start backfill trained on, so the live seam
    /// bar is never learned twice.</summary>
    private DateTime _lastBackfillBarStart = DateTime.MinValue;

    /// <summary>How many bars of stored tape the warm-start replays (capped at 24 h of span).</summary>
    private const int WarmStartBars = 200;

    /// <summary>Set by trade ingest, cleared by the render tick — coalesces many trades into one
    /// canvas rebuild so a fast tape can't pin the UI thread (the old per-trade redraw both pinned
    /// the UI and let the unbounded trade channel pile up into GBs of backlog).</summary>
    private bool _dirty;

    /// <summary>Hard cap on the trade channel so a burst the UI can't drain is bounded in memory
    /// (oldest prints drop). Batched draining keeps it far from full in normal flow.</summary>
    private const int TradeChannelCapacity = 100_000;

    /// <summary>Max trades processed in a single UI-thread marshal before yielding, so draining a
    /// large backlog stays responsive.</summary>
    private const int MaxDrainBatch = 8_192;

    /// <summary>Wall-clock arrival times of recent trades, pruned to <see cref="TicksWindow"/>, so the
    /// stats panel can show a live ticks-per-second throughput that decays when flow stops.</summary>
    private readonly Queue<DateTime> _tradeArrivals = new();
    private static readonly TimeSpan TicksWindow = TimeSpan.FromSeconds(2);

    /// <summary>Drives both the ticks/sec decay and the coalesced canvas redraw (~12 fps). Trade
    /// ingest only marks the chart dirty; this timer does the (expensive) rebuild + redraw.</summary>
    private readonly IDisposable _renderTimer;

    /// <summary>False until the constructor has built every collection. Suppresses the
    /// <c>[ObservableProperty]</c> setters' On*Changed callbacks from running <see cref="Restart"/>
    /// against half-initialized state.</summary>
    private bool _ready;

    /// <summary>Selectable bar intervals — label + the time bucket each footprint bar spans.</summary>
    public sealed record FootprintInterval(string Label, TimeSpan Span);

    private static readonly IReadOnlyList<FootprintInterval> AllIntervals = new[]
    {
        new FootprintInterval("15s", TimeSpan.FromSeconds(15)),
        new FootprintInterval("30s", TimeSpan.FromSeconds(30)),
        new FootprintInterval("1m",  TimeSpan.FromMinutes(1)),
        new FootprintInterval("5m",  TimeSpan.FromMinutes(5)),
    };

    public VolumeFootprintViewModel(
        IMarketDataRepository repository,
        IMarketDataHub hub,
        IMarketDataIngest ingest,
        IMarketDataStore store,
        IBrokerSelector selector,
        InMemoryLogSink log,
        ILogger<VolumeFootprintViewModel> logger)
    {
        _repository = repository;
        _hub = hub;
        _ingest = ingest;
        _store = store;
        _selector = selector;
        _log = log;
        _logger = logger;

        // Build every collection BEFORE assigning the selected values: the generated property
        // setters call the On*Changed handlers, which would otherwise fire Restart() (and touch
        // Bars) before it exists. The _ready guard suppresses those mid-construction callbacks; we
        // kick off a single Restart() explicitly once everything is wired.
        _allInstruments = SignalInstrumentCatalog.All;
        Bars = new ObservableCollection<RenderBar>();
        Instruments = new ObservableCollection<SignalInstrument>(_allInstruments.Take(MaxInstrumentsDisplayed));
        Intervals = new ObservableCollection<FootprintInterval>(AllIntervals);

        SelectedInstrument = Instruments.FirstOrDefault(i => i.Contract.Symbol == "SPY")
                             ?? Instruments.FirstOrDefault();
        SelectedInterval = Intervals.First(i => i.Label == "1m");

        // Coalesced render tick (~12 fps) via the portable timer seam. IDisposable, owned by this VM.
        _renderTimer = UiThread.CreateRenderTimer(TimeSpan.FromMilliseconds(80), OnRenderTick);

        _ready = true;
        _ = LoadInstrumentsAsync();
        Restart();
    }

    public ObservableCollection<SignalInstrument> Instruments { get; }
    public ObservableCollection<FootprintInterval> Intervals { get; }

    /// <summary>Cell rendering modes shown in the toolbar combo (Bid×Ask / Delta / Volume).</summary>
    public IReadOnlyList<CellDisplayMode> DisplayModes { get; } =
        new[] { CellDisplayMode.BidAsk, CellDisplayMode.Delta, CellDisplayMode.Volume };

    /// <summary>Most recent footprint bars, oldest first (rendered left → right).</summary>
    public ObservableCollection<RenderBar> Bars { get; }

    [ObservableProperty] private SignalInstrument? _selectedInstrument;
    [ObservableProperty] private FootprintInterval? _selectedInterval;
    [ObservableProperty] private string _instrumentSearchText = string.Empty;
    [ObservableProperty] private string _tickSizeText = "0.25";
    [ObservableProperty] private int _maxBars = 14;
    [ObservableProperty] private string _status = "Pick an instrument to stream its footprint.";
    [ObservableProperty] private long _tradesSeen;
    [ObservableProperty] private long _sessionDelta;

    // ── Top-right stats panel ──────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _pocSlopeText = "—";
    /// <summary>Sign of the POC-regression slope: +1 rising, -1 falling, 0 flat (drives the colour).</summary>
    [ObservableProperty] private int _pocSlopeDirection;
    [ObservableProperty] private string _buyPocSlopeText = "—";
    [ObservableProperty] private int _buyPocSlopeDirection;
    [ObservableProperty] private string _sellPocSlopeText = "—";
    [ObservableProperty] private int _sellPocSlopeDirection;
    [ObservableProperty] private int _cvdDirection;
    [ObservableProperty] private string _ticksPerSecondText = "0.0";
    [ObservableProperty] private string _visibleVolumeText = "0";
    [ObservableProperty] private string _buySellText = "—";
    [ObservableProperty] private string _currentPocText = "—";
    /// <summary>Stacked-imbalance run counts for the last visible bar ("buy / sell"), from Core.</summary>
    [ObservableProperty] private string _stackedText = "—";

    // ── Cell display + analytics overlays (redraw-only; no stream restart) ────────────────────
    [ObservableProperty] private CellDisplayMode _selectedDisplayMode = CellDisplayMode.BidAsk;
    /// <summary>Highlight Core's diagonal bid/ask imbalances and stacked runs on the cells.</summary>
    [ObservableProperty] private bool _showImbalances = true;
    /// <summary>Shade each bar's 70% value area (VAH↔VAL band).</summary>
    [ObservableProperty] private bool _showValueArea = true;
    /// <summary>Draw the right-edge composite session volume profile across the visible bars.</summary>
    [ObservableProperty] private bool _showVolumeProfile = true;
    /// <summary>Show the per-cell volume figures (off = colour-only heatmap).</summary>
    [ObservableProperty] private bool _showCellText = true;
    /// <summary>Vertical zoom — scales the per-row pixel height in the renderer (1 = default).</summary>
    [ObservableProperty] private double _zoom = 1.0;

    // ── Regression overlay toggles (one checkbox each in the toolbar) ─────────────────────────
    [ObservableProperty] private bool _showLinearFit = true;
    [ObservableProperty] private bool _showQuadraticFit;
    [ObservableProperty] private bool _showCubicFit;
    [ObservableProperty] private bool _showTheilSenFit;
    [ObservableProperty] private bool _showExponentialFit;
    [ObservableProperty] private bool _showLogarithmicFit;
    [ObservableProperty] private bool _showLowessFit;

    // ── Virtual predictor (forward extrapolation of the enabled fits) ─────────────────────────
    [ObservableProperty] private bool _showPredictedBars = true;
    [ObservableProperty] private int _predictionBars = 5;

    // ── ML predictor (online-learned next-bar forecast; see FootprintNextBarPredictor) ────────
    [ObservableProperty] private bool _showMlPrediction = true;
    /// <summary>Replay recent stored tape through the predictor on (re)start so it isn't cold.</summary>
    [ObservableProperty] private bool _warmStartFromHistory = true;
    [ObservableProperty] private string _mlSamplesText = "0";
    [ObservableProperty] private string _mlMaeText = "—";
    [ObservableProperty] private string _mlHitRateText = "—";
    [ObservableProperty] private string _regMaeText = "—";
    [ObservableProperty] private string _regHitRateText = "—";
    /// <summary>+1 when the ML forecaster is beating the regression consensus on rolling MAE,
    /// −1 when it is losing, 0 while either side lacks scores (drives the stats-row colour).</summary>
    [ObservableProperty] private int _mlEdgeDirection;

    /// <summary>Fitted overlay curves (one ŷ per column, extended by the prediction horizon when
    /// the predictor is on) for every enabled fit kind × POC series. Rebuilt on each trade and on
    /// checkbox toggles; read by the code-behind.</summary>
    public IReadOnlyList<PocFitCurve> FitCurves { get; private set; } = Array.Empty<PocFitCurve>();

    /// <summary>Virtual predicted columns past the last bar — per-series consensus (mean) of every
    /// enabled fit extrapolated forward. Empty when the predictor is off or no fit is enabled.</summary>
    public IReadOnlyList<PredictedBar> Predicted { get; private set; } = Array.Empty<PredictedBar>();

    /// <summary>ML-forecast future columns past the last bar, capped at the model's horizon
    /// (shorter than the regression's — direct per-horizon learners don't extrapolate far).
    /// Empty while the model warms up or when the toggle is off.</summary>
    public IReadOnlyList<MlPredictedBar> MlPredicted { get; private set; } = Array.Empty<MlPredictedBar>();

    /// <summary>Least-squares slope of POC price against bar (column) index across the visible bars,
    /// in price units per bar. Feeds the stats-panel slope read-out.</summary>
    public double PocSlope { get; private set; }

    /// <summary>Intercept of the POC regression at column 0, in price units.</summary>
    public double PocIntercept { get; private set; }

    /// <summary>True once ≥2 bars carry a valid POC, so a regression line can be drawn.</summary>
    public bool HasRegression { get; private set; }

    /// <summary>Buy-POC regression (slope/intercept/validity), same convention as the total POC.</summary>
    public double BuyPocSlope { get; private set; }
    public double BuyPocIntercept { get; private set; }
    public bool HasBuyRegression { get; private set; }

    /// <summary>Sell-POC regression (slope/intercept/validity), same convention as the total POC.</summary>
    public double SellPocSlope { get; private set; }
    public double SellPocIntercept { get; private set; }
    public bool HasSellRegression { get; private set; }

    /// <summary>Raised on the UI thread whenever the bar set changes and the canvas should redraw.</summary>
    public event EventHandler? FootprintChanged;

    private double TickSize => double.TryParse(TickSizeText, out var t) && t > 0 ? t : 0.25;

    partial void OnInstrumentSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedInstrumentChanged(SignalInstrument? value) { if (_ready) Restart(); }
    partial void OnSelectedIntervalChanged(FootprintInterval? value) { if (_ready) Restart(); }
    partial void OnTickSizeTextChanged(string value) { if (_ready) Restart(); }

    partial void OnShowLinearFitChanged(bool value) => RefreshFitCurves();
    partial void OnShowQuadraticFitChanged(bool value) => RefreshFitCurves();
    partial void OnShowCubicFitChanged(bool value) => RefreshFitCurves();
    partial void OnShowTheilSenFitChanged(bool value) => RefreshFitCurves();
    partial void OnShowExponentialFitChanged(bool value) => RefreshFitCurves();
    partial void OnShowLogarithmicFitChanged(bool value) => RefreshFitCurves();
    partial void OnShowLowessFitChanged(bool value) => RefreshFitCurves();
    partial void OnShowPredictedBarsChanged(bool value) => RefreshFitCurves();
    partial void OnPredictionBarsChanged(int value)
    {
        RefreshFitCurves();
        PublishMlForecast();
    }

    partial void OnShowMlPredictionChanged(bool value)
    {
        PublishMlForecast();
        RaiseRedraw();
    }

    // Display-mode / overlay / zoom toggles only change presentation — redraw, don't restart.
    partial void OnSelectedDisplayModeChanged(CellDisplayMode value) => RaiseRedraw();
    partial void OnShowImbalancesChanged(bool value) => RaiseRedraw();
    partial void OnShowValueAreaChanged(bool value) => RaiseRedraw();
    partial void OnShowVolumeProfileChanged(bool value) => RaiseRedraw();
    partial void OnShowCellTextChanged(bool value) => RaiseRedraw();
    partial void OnZoomChanged(double value) => RaiseRedraw();

    private void RaiseRedraw()
    {
        if (_ready) FootprintChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadInstrumentsAsync()
    {
        try
        {
            var list = await _repository.ListInstrumentsAsync();
            if (list is null || list.Count == 0) return;
            _allInstruments = list
                .Select(i => new SignalInstrument(i.DisplayName, i.Category, i.Contract, i.Broker))
                .ToList();
            var keep = SelectedInstrument;
            ApplyFilter();
            SelectedInstrument = keep is not null && Instruments.Contains(keep)
                ? keep
                : _allInstruments.FirstOrDefault(i => i.Contract.Symbol == "SPY") ?? _allInstruments.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Footprint: instrument list load failed; using static catalog");
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
        var instrument = SelectedInstrument;
        var interval = SelectedInterval;
        if (instrument is null || interval is null) return;

        BrokerKind broker;
        try { broker = ResolveBroker(instrument); }
        catch (InvalidOperationException ex) { Status = ex.Message; ClearBars(); return; }

        _useSynthetic = !BrokerSupportsTradeTape(broker);
        _synth.Reset();
        _bucketer = new FootprintTimeBucketer(interval.Span, TickSize,
            _useSynthetic ? FeedQuality.SyntheticL1 : FeedQuality.RealTape);
        _ml = null;
        _lastBackfillBarStart = DateTime.MinValue;
        _dirty = false;
        TradesSeen = 0;
        SessionDelta = 0;
        ResetStats();
        ClearBars();

        var tape = _useSynthetic ? "synthetic L1-derived" : "real trade tape";
        Status = $"Streaming {instrument.DisplayName} ({BrokerLabel(broker)}) — {tape}, {interval.Label} bars @ {TickSize} tick…";
        _log.Append(LogSource, _useSynthetic ? "WARN" : "INFO",
            $"Footprint on {instrument.DisplayName} [{BrokerLabel(broker)}] — {tape}, {interval.Label} bars, tick {TickSize}");

        _streamCts = new CancellationTokenSource();
        _ = RunStreamAsync(instrument.Contract, broker, interval.Span, TickSize, _streamCts.Token);
    }

    private async Task RunStreamAsync(Contract contract, BrokerKind broker, TimeSpan span,
        double tickSize, CancellationToken ct)
    {
        InstrumentId instrumentId;
        try
        {
            instrumentId = _ingest.Resolve(contract, broker);
            // Quote pump always runs: it carries bid/ask context for the real tape's Lee-Ready
            // classification and is the source for the synthesizer in fallback mode.
            _quoteHandle = _ingest.Subscribe(contract, broker);
            if (!_useSynthetic) _tradeHandle = _ingest.SubscribeTrades(contract, broker);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Footprint: subscribe failed for {Symbol}", contract.Symbol);
            await UiThread.RunAsync(() => Status = $"Subscribe failed: {ex.Message}");
            return;
        }

        // Bounded + DropOldest: if a burst outruns the UI drain the channel can never grow without
        // limit (it caps at TradeChannelCapacity and sheds the oldest prints) — this is the hard
        // backstop against the old unbounded-channel memory pile-up.
        var channel = Channel.CreateBounded<TradePrint>(new BoundedChannelOptions(TradeChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        using var sub = _useSynthetic
            ? _hub.Quotes(instrumentId).Subscribe(q =>
              {
                  var t = _synth.Synthesize(q);
                  if (t is not null) channel.Writer.TryWrite(t);
              })
            : _hub.Trades(instrumentId).Subscribe(t => channel.Writer.TryWrite(t));

        // Warm-start the ML forecaster from stored tape before the drain loop begins. Live prints
        // buffer in the bounded channel meanwhile (DropOldest keeps that safe); the ML engine is
        // only published to _ml once training is done, so live seals can never race the backfill.
        var ml = new FootprintNextBarPredictor(tickSize);
        if (WarmStartFromHistory)
        {
            try
            {
                await WarmStartAsync(instrumentId, broker, span, tickSize, ml, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Footprint: ML warm-start failed for {Symbol}; starting cold", contract.Symbol);
            }
        }
        if (ct.IsCancellationRequested) return;
        _ml = ml;
        UpdateMlStats();

        // Drain in batches: one UI-thread marshal per batch instead of one per trade. Aggregation
        // into the forming bucket is cheap; the (expensive) canvas rebuild is left to the render
        // timer via the dirty flag, so trade rate is fully decoupled from redraw rate.
        var batch = new List<TradePrint>(MaxDrainBatch);
        try
        {
            while (await channel.Reader.WaitToReadAsync(ct))
            {
                batch.Clear();
                while (batch.Count < MaxDrainBatch && channel.Reader.TryRead(out var trade))
                    batch.Add(trade);
                if (batch.Count == 0) continue;
                await UiThread.RunAsync(() => IngestBatch(batch));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Footprint: trade stream ended");
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    /// <summary>Trains the fresh predictor through recent stored tape (interval × <see cref="WarmStartBars"/>
    /// bars, capped at 24 h) so it is useful from the first live seal instead of needing ~20 live
    /// bars. Bars are rebuilt through the same <see cref="FootprintTimeBucketer"/> path the live
    /// stream uses. Runs entirely on a thread-pool thread against local state — the engine is only
    /// published to <see cref="_ml"/> by the caller after this completes, so nothing is shared. A
    /// missing/empty store simply trains on nothing and the model starts cold.</summary>
    private async Task WarmStartAsync(InstrumentId instrumentId, BrokerKind broker, TimeSpan span,
        double tickSize, FootprintNextBarPredictor ml, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var to = new DateTime(now.Ticks - now.Ticks % span.Ticks, DateTimeKind.Utc);
        var lookbackTicks = Math.Min(span.Ticks * WarmStartBars, TimeSpan.FromHours(24).Ticks);
        var from = to - TimeSpan.FromTicks(lookbackTicks);

        var trainedBars = 0;
        var lastSealedStart = DateTime.MinValue;
        await Task.Run(async () =>
        {
            var bucketer = new FootprintTimeBucketer(span, tickSize, FeedQuality.RealTape);
            await foreach (var trade in _store.ReadTradesAsync(instrumentId, from, to, broker, ct))
            {
                if (bucketer.Add(FootprintPrint.From(trade)) is not { } sealedCore) continue;
                ml.OnBarSealed(Summarize(new RenderBar(sealedCore)), double.NaN);
                lastSealedStart = sealedCore.StartUtc;
                trainedBars++;
            }
        }, ct);

        _lastBackfillBarStart = lastSealedStart;
        if (trainedBars > 0)
            _log.Append(LogSource, "INFO",
                $"ML warm-start: trained on {trainedBars} stored bars ({(ml.IsReady ? "ready" : "still cold")})");
    }

    /// <summary>Republishes <see cref="MlPredicted"/> from the engine's latest forecast, clamped
    /// to the user's horizon, and refreshes the stats read-outs. Cheap; safe to call from toggle
    /// handlers as well as the seal path.</summary>
    private void PublishMlForecast()
    {
        var forecast = _ml?.LastForecast ?? Array.Empty<FootprintForecastBar>();
        if (!ShowMlPrediction || forecast.Count == 0)
        {
            MlPredicted = Array.Empty<MlPredictedBar>();
        }
        else
        {
            var take = Math.Min(forecast.Count, Math.Clamp(PredictionBars, 1, 60));
            var columns = new MlPredictedBar[take];
            for (var i = 0; i < take; i++)
            {
                var f = forecast[i];
                columns[i] = new MlPredictedBar(f.Poc, f.BuyPoc, f.SellPoc, f.TotalVolume, f.Delta, f.Horizon);
            }
            MlPredicted = columns;
        }
        UpdateMlStats();
    }

    private void UpdateMlStats()
    {
        if (_ml is null)
        {
            MlSamplesText = "0";
            MlMaeText = MlHitRateText = RegMaeText = RegHitRateText = "—";
            MlEdgeDirection = 0;
            return;
        }

        MlSamplesText = _ml.SamplesSeen.ToString("N0", CultureInfo.InvariantCulture);
        var ml = _ml.MlAccuracy;
        var reg = _ml.BaselineAccuracy;
        (MlMaeText, MlHitRateText) = FormatAccuracy(ml);
        (RegMaeText, RegHitRateText) = FormatAccuracy(reg);
        MlEdgeDirection = ml.ScoredCount >= 10 && reg.ScoredCount >= 10
            ? Math.Sign(reg.PocMaeTicks - ml.PocMaeTicks)
            : 0;
    }

    private static (string mae, string hit) FormatAccuracy(ForecastAccuracy accuracy) =>
        accuracy.ScoredCount == 0
            ? ("—", "—")
            : (accuracy.PocMaeTicks.ToString("0.00", CultureInfo.InvariantCulture) + " t",
               (accuracy.DirectionalHitRate * 100).ToString("0", CultureInfo.InvariantCulture) + "%");

    /// <summary>Ingests a drained batch of trades on the UI thread: cheap per-trade accumulation
    /// (bucket roll/seal + append to the forming bucket) only. The expensive forming-bar rebuild,
    /// stats and canvas redraw are deferred to <see cref="OnRenderTick"/> via the dirty flag, so a
    /// fast tape can't pin the UI thread or back the channel up.</summary>
    private void IngestBatch(List<TradePrint> batch)
    {
        if (_bucketer is null) return;
        var now = DateTime.UtcNow;

        foreach (var trade in batch)
        {
            if (_bucketer.Add(FootprintPrint.From(trade)) is { } sealedCore)
                OnBarSealed(sealedCore);
            TradesSeen++;
            _tradeArrivals.Enqueue(now);
        }

        _dirty = true;
    }

    /// <summary>Folds a sealed Core bar into the visible bars, then runs the ML step: score the
    /// previous forecast against this realized bar, learn, and predict the next horizon. The
    /// regression consensus published <em>before</em> this seal is captured as the baseline —
    /// a genuine ex-ante forecast made from the bars visible before this bar completed. Seals
    /// happen at most once per interval, so this stays off the hot per-trade path.</summary>
    private void OnBarSealed(FootprintBar sealedCore)
    {
        var finalized = new RenderBar(sealedCore);
        if (Bars.Count > 0 && Bars[^1].StartUtc == sealedCore.StartUtc) Bars[^1] = finalized;
        else Bars.Add(finalized);
        while (Bars.Count > Math.Max(2, MaxBars)) Bars.RemoveAt(0);

        if (_ml is null || sealedCore.StartUtc <= _lastBackfillBarStart) return;
        var baseline = Predicted.Count > 0 ? Predicted[0].Poc : double.NaN;
        _ml.OnBarSealed(Summarize(finalized), baseline);
        PublishMlForecast();
    }

    private static FootprintBarSummary Summarize(RenderBar bar) =>
        FootprintBarSummary.From(bar.Core, bar.BuyPointOfControl, bar.SellPointOfControl,
            bar.ValueAreaHigh, bar.ValueAreaLow);

    /// <summary>Render tick (~12 fps): if trades arrived since the last tick, rebuild the forming
    /// bar once from the accumulated prints, refresh the stats and redraw the canvas. Always decays
    /// the ticks/sec read-out so it falls toward zero when the tape goes quiet.</summary>
    private void OnRenderTick()
    {
        UpdateTicksPerSecond();
        if (!_dirty) return;
        _dirty = false;

        if (_bucketer?.BuildForming() is not { } formingCore) return;
        var formingBar = new RenderBar(formingCore);

        if (Bars.Count > 0 && Bars[^1].StartUtc == formingCore.StartUtc) Bars[^1] = formingBar;
        else
        {
            Bars.Add(formingBar);
            while (Bars.Count > Math.Max(2, MaxBars)) Bars.RemoveAt(0);
        }

        SessionDelta = formingCore.CumulativeDelta;
        UpdateStats();
        Status = $"{SelectedInstrument?.DisplayName} · {Bars.Count} bars · {TradesSeen} trades · CVD {SessionDelta:+#;-#;0}";
        FootprintChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Recomputes the POC regression and the stats-panel read-outs from the visible bars.
    /// Called on each trade (UI thread) before the canvas redraws. Cheap — O(visible bars).</summary>
    private void UpdateStats()
    {
        var bars = Bars;
        int n = bars.Count;

        // Least-squares fit of each POC flavour (price vs column index) over bars with a valid POC.
        (PocSlope, PocIntercept, HasRegression)         = FitPoc(b => b.PointOfControl);
        (BuyPocSlope, BuyPocIntercept, HasBuyRegression) = FitPoc(b => b.BuyPointOfControl);
        (SellPocSlope, SellPocIntercept, HasSellRegression) = FitPoc(b => b.SellPointOfControl);

        (PocSlopeText, PocSlopeDirection)     = FormatSlope(HasRegression, PocSlope);
        (BuyPocSlopeText, BuyPocSlopeDirection)   = FormatSlope(HasBuyRegression, BuyPocSlope);
        (SellPocSlopeText, SellPocSlopeDirection) = FormatSlope(HasSellRegression, SellPocSlope);

        // Buy/sell split from Core bar fields.
        long buy = 0, sell = 0;
        foreach (var b in bars) { buy += b.Core.BuyVolume; sell += b.Core.SellVolume; }
        var vol = buy + sell;
        VisibleVolumeText = vol.ToString("N0", CultureInfo.InvariantCulture);
        BuySellText = vol > 0
            ? $"{100.0 * buy / vol:0}% / {100.0 * sell / vol:0}%"
            : "—";

        var lastPoc = n > 0 ? bars[n - 1].PointOfControl : double.NaN;
        CurrentPocText = double.IsNaN(lastPoc)
            ? "—"
            : lastPoc.ToString("N" + DecimalsFor(TickSize), CultureInfo.InvariantCulture);

        // Stacked-imbalance runs for the most recent bar (Core already counts these).
        StackedText = n > 0 ? $"{bars[n - 1].StackedBuy} / {bars[n - 1].StackedSell}" : "—";

        CvdDirection = Math.Sign(SessionDelta);

        RecomputeFitCurves();
    }

    /// <summary>Toggle-handler path: recompute the overlay curves and ask the canvas to redraw
    /// immediately instead of waiting for the next trade.</summary>
    private void RefreshFitCurves()
    {
        if (!_ready) return;
        RecomputeFitCurves();
        FootprintChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Fits every enabled <see cref="CurveFitKind"/> to each POC series (total/buy/sell)
    /// and publishes the sampled curves for the overlay. Skips bars whose POC is NaN, but always
    /// evaluates across the full visible column range so curves span the chart. When the predictor
    /// is on, every fit is also evaluated <see cref="PredictionBars"/> columns past the last bar
    /// and the per-series consensus (mean of the enabled fits) becomes <see cref="Predicted"/>.</summary>
    private void RecomputeFitCurves()
    {
        var bars = Bars;
        var n = bars.Count;
        var kinds = EnabledFitKinds();
        if (n < 2 || kinds.Count == 0)
        {
            FitCurves = Array.Empty<PocFitCurve>();
            Predicted = Array.Empty<PredictedBar>();
            return;
        }

        var horizon = ShowPredictedBars ? Math.Clamp(PredictionBars, 1, 60) : 0;
        var columns = n + horizon;
        var evalX = new double[columns];
        for (var i = 0; i < columns; i++) evalX[i] = i;

        var curves = new List<PocFitCurve>();
        var totalFits = AddCurves(PocSeries.Total, b => b.PointOfControl);
        var buyFits = AddCurves(PocSeries.Buy, b => b.BuyPointOfControl);
        var sellFits = AddCurves(PocSeries.Sell, b => b.SellPointOfControl);
        FitCurves = curves;

        if (horizon == 0 || curves.Count == 0)
        {
            Predicted = Array.Empty<PredictedBar>();
            return;
        }
        var predicted = new PredictedBar[horizon];
        for (var j = 0; j < horizon; j++)
            predicted[j] = new PredictedBar(
                ConsensusAt(totalFits, n + j),
                ConsensusAt(buyFits, n + j),
                ConsensusAt(sellFits, n + j));
        Predicted = predicted;

        List<double[]> AddCurves(PocSeries series, Func<RenderBar, double> selector)
        {
            var xs = new List<double>(n);
            var ys = new List<double>(n);
            for (var i = 0; i < n; i++)
            {
                var p = selector(bars[i]);
                if (double.IsNaN(p)) continue;
                xs.Add(i);
                ys.Add(p);
            }
            var fits = new List<double[]>(kinds.Count);
            foreach (var kind in kinds)
            {
                var fitted = CurveFitting.FitEvaluate(kind, xs, ys, evalX);
                if (fitted is null) continue;
                curves.Add(new PocFitCurve(kind, series, fitted));
                fits.Add(fitted);
            }
            return fits;
        }

        static double ConsensusAt(List<double[]> fits, int column)
        {
            double sum = 0;
            var count = 0;
            foreach (var f in fits)
            {
                var v = f[column];
                if (!double.IsFinite(v)) continue;
                sum += v;
                count++;
            }
            return count > 0 ? sum / count : double.NaN;
        }
    }

    private List<CurveFitKind> EnabledFitKinds()
    {
        var kinds = new List<CurveFitKind>(7);
        if (ShowLinearFit) kinds.Add(CurveFitKind.Linear);
        if (ShowQuadraticFit) kinds.Add(CurveFitKind.Quadratic);
        if (ShowCubicFit) kinds.Add(CurveFitKind.Cubic);
        if (ShowTheilSenFit) kinds.Add(CurveFitKind.TheilSen);
        if (ShowExponentialFit) kinds.Add(CurveFitKind.Exponential);
        if (ShowLogarithmicFit) kinds.Add(CurveFitKind.Logarithmic);
        if (ShowLowessFit) kinds.Add(CurveFitKind.Lowess);
        return kinds;
    }

    /// <summary>Least-squares fit of a chosen POC price (y) against column index (x) over the visible
    /// bars, skipping bars whose POC is NaN. Returns (slope price/bar, intercept at column 0, valid).</summary>
    private (double slope, double intercept, bool has) FitPoc(Func<RenderBar, double> selector)
    {
        var bars = Bars;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        int m = 0;
        for (var i = 0; i < bars.Count; i++)
        {
            var p = selector(bars[i]);
            if (double.IsNaN(p)) continue;
            sx += i; sy += p; sxx += (double)i * i; sxy += i * p; m++;
        }
        var denom = m * sxx - sx * sx;
        if (m < 2 || Math.Abs(denom) < 1e-9) return (0, 0, false);
        var slope = (m * sxy - sx * sy) / denom;
        return (slope, (sy - slope * sx) / m, true);
    }

    private static (string text, int direction) FormatSlope(bool has, double slope) =>
        has
            ? ($"{slope:+0.####;-0.####;0} /bar", slope > 1e-9 ? 1 : slope < -1e-9 ? -1 : 0)
            : ("—", 0);

    /// <summary>Prunes the arrival window and republishes the live ticks/sec rate. Runs on the stats
    /// timer so the figure decays toward zero when the tape goes quiet.</summary>
    private void UpdateTicksPerSecond()
    {
        var cutoff = DateTime.UtcNow - TicksWindow;
        while (_tradeArrivals.Count > 0 && _tradeArrivals.Peek() < cutoff) _tradeArrivals.Dequeue();
        var rate = _tradeArrivals.Count / TicksWindow.TotalSeconds;
        TicksPerSecondText = rate.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private void ResetStats()
    {
        _tradeArrivals.Clear();
        FitCurves = Array.Empty<PocFitCurve>();
        Predicted = Array.Empty<PredictedBar>();
        PocSlope = 0; PocIntercept = 0; HasRegression = false;
        BuyPocSlope = 0; BuyPocIntercept = 0; HasBuyRegression = false;
        SellPocSlope = 0; SellPocIntercept = 0; HasSellRegression = false;
        PocSlopeText = "—"; PocSlopeDirection = 0;
        BuyPocSlopeText = "—"; BuyPocSlopeDirection = 0;
        SellPocSlopeText = "—"; SellPocSlopeDirection = 0;
        CvdDirection = 0;
        TicksPerSecondText = "0.0"; VisibleVolumeText = "0"; BuySellText = "—"; CurrentPocText = "—";
        StackedText = "—";
        MlPredicted = Array.Empty<MlPredictedBar>();
        MlSamplesText = "0";
        MlMaeText = MlHitRateText = RegMaeText = RegHitRateText = "—";
        MlEdgeDirection = 0;
    }

    private static int DecimalsFor(double tick)
    {
        if (tick >= 1) return 0;
        var decimals = 0;
        while (tick < 1 && decimals < 8) { tick *= 10; decimals++; }
        return decimals;
    }

    private void ClearBars()
    {
        Bars.Clear();
        _bucketer?.Reset();
        _dirty = false;
        FootprintChanged?.Invoke(this, EventArgs.Empty);
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
        _quoteHandle?.Dispose(); _quoteHandle = null;
        _tradeHandle?.Dispose(); _tradeHandle = null;
    }

    public void Dispose()
    {
        _renderTimer.Dispose();
        StopStream();
    }
}

/// <summary>
/// L1 tick-rule synthesizer: derives <see cref="TradePrint"/>s from a quote stream when the broker
/// has no native trade tape. Mid ticks up ⇒ buy print at the ask (size = ask size); mid ticks down ⇒
/// sell print at the bid (size = bid size); unchanged mid ⇒ no event. Mirrors the strategy projects'
/// internal synthesizer; kept local so the App doesn't reach into a strategy assembly.
/// </summary>
internal sealed class QuoteTradeSynthesizer
{
    private Quote? _prev;

    public TradePrint? Synthesize(Quote q)
    {
        var prev = _prev;
        _prev = q;
        if (prev is null || q.Mid == prev.Mid) return null;
        var isBuy = q.Mid > prev.Mid;
        var price = isBuy ? q.Ask : q.Bid;
        var size = Math.Max(1L, isBuy ? q.AskSize : q.BidSize);
        return new TradePrint(q.InstrumentId, q.EventTimeUtc, q.IngestTimeUtc, price, size,
            isBuy ? AggressorSide.Buy : AggressorSide.Sell, q.Source, q.Sequence, q.EventTimeApproximate);
    }

    public void Reset() => _prev = null;
}
