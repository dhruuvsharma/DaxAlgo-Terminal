using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Quant;
using TradingTerminal.Core.Strategies.Apex;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// Snapshot of the strategy's in-progress price candle for the live UI. Updated on every tick;
/// the user-visible widget rebinds at the redraw timer's cadence rather than per tick.
/// </summary>
public sealed record ApexLiveCandle(
    DateTime OpenTimeUtc,
    double Open,
    double High,
    double Low,
    double Close,
    long Volume,
    long BuyVolume,
    long SellVolume,
    long Delta,
    double DeltaEfficiency);

/// <summary>
/// APEX microstructure scalper — v2. A trade-tape-primary order-flow scalper that fuses a set of
/// flow estimators into a single composite, calibrates that composite onto realized forward
/// return, and trades only when the calibrated edge clears full round-trip costs.
///
/// <para><b>Data foundation.</b> The primary input is the real trade tape via
/// <see cref="OnTradeAsync"/> — every flow signal is computed from true aggressor-side volume,
/// tagged <see cref="FeedQuality.RealTape"/> (q = 1.0). When no tape arrives the engine
/// synthesizes pseudo-prints from L1 quotes via <see cref="FootprintFeatures.SyntheticPrints"/>,
/// tagged <see cref="FeedQuality.SyntheticL1"/> (q ≈ 0.4); every signal then multiplies its
/// confidence by q. Windowed flow statistics run in <b>volume time</b> (constant-volume buckets,
/// adaptive via <see cref="VolumeTimeBucketer.AdaptiveBucketVolume"/>); wall-clock survives only
/// in TTLs and session gates. Footprint aggregation is delegated to
/// <see cref="FootprintFeatures.BuildBar"/> — the single source of truth shared with the chart.</para>
///
/// <para><b>Signals.</b> Delta (z of bar Δ + z of Δ-acceleration), VPIN (constant-volume buckets,
/// τ = 2|f − ½|), footprint stacked imbalance (Core 3:1 diagonal rule), tape speed (arrival-rate z
/// with an up-tick directional gate), Kyle-λ residual (rolling OLS r = λΔ + ε; ε_cum z-score, λ̂ a
/// thinness/risk feature), a triple regression-line block over the buy/sell centroid + POC lines
/// (initiative contrast, control coordinate ρ, control rotation dρ/dt, wedge dynamics w + dw/dt,
/// value deviation z_p), and CVD divergence. OBI is computed only when a genuine depth snapshot
/// stream is present (<see cref="OnDepthAsync"/>) and fresh.</para>
///
/// <para><b>Combination.</b> No hand weights: a rolling Ledoit-Wolf signal covariance Σ and a
/// per-signal information coefficient (IC) vs forward return drive <c>w = Σ⁻¹·IC</c>
/// (<see cref="SignalWeights.Solve"/>, negative hedge weights allowed). A regime multiplier layer
/// (ADX + BB-width, augmented by wedge dynamics) and a TTL staleness layer modulate the blend:
/// <c>C = Σ wᵢ mᵢ confᵢ Sᵢ / Σ wᵢ mᵢ</c>.</para>
///
/// <para><b>Entry / exit / sizing.</b> The composite is calibrated to expected forward return via
/// isotonic regression (<see cref="IsotonicCalibration"/>); a trade fires only when
/// <c>g(C) ≥ spread + 2·fee + E[slip | C]</c> in direction sign(C). Until enough samples exist the
/// engine runs in <b>bootstrap mode</b> behind a configurable composite threshold (flagged in the
/// snapshot). Stops/targets anchor to flow structure (wedge boundaries / value-area edges) and are
/// gated by a first-passage EV check (<see cref="FirstPassage"/>). Sizing is risk-fraction capped
/// at quarter-Kelly on the conditional edge, scaled down when λ̂ is elevated. All P&amp;L tracking is
/// cost-inclusive. When only synthetic tape is available the engine runs heavily discounted (q on
/// every signal) and is exposed via <see cref="ApexSnapshotV2.FeedQuality"/>.</para>
///
/// Self-scales via dimensionless coefficients in <see cref="ApexV2Options"/>: price distances scale
/// √(span/span₀), volume thresholds linear in median bar volume, TTLs = α·span, footprint tick ≈
/// ATR_bar / target-rows. Defaults target CME micro futures (MES/MNQ, 15s–1m bars).
/// </summary>
public sealed class ApexScalperStrategy : IBacktestStrategy
{
    // ── Identity ─────────────────────────────────────────────────────────────────────
    public const string SigDelta = "DELTA";
    public const string SigVpin = "VPIN";
    public const string SigFootprint = "FOOTPRINT";
    public const string SigTapeSpeed = "TAPE_SPEED";
    public const string SigKyle = "KYLE";
    public const string SigInitiative = "INITIATIVE";
    public const string SigControl = "CONTROL";
    public const string SigWedge = "WEDGE";
    public const string SigValue = "VALUE";
    public const string SigCvd = "CVD";
    public const string SigObi = "OBI";
    public const string SigPredNode = "PRED_NODE";

    /// <summary>The canonical signal order the engine scores/logs/weights. OBI participates only when
    /// a real depth stream is live; PRED_NODE is the Kalman-forecast node-migration signal appended
    /// last.</summary>
    public static readonly string[] SignalNames =
    {
        SigDelta, SigVpin, SigFootprint, SigTapeSpeed, SigKyle,
        SigInitiative, SigControl, SigWedge, SigValue, SigCvd, SigObi, SigPredNode,
    };

    // ── Configuration ──────────────────────────────────────────────────────────────────
    public ApexV2Options Options { get; }
    public TimeSpan CandleInterval { get; }

    /// <summary>Instrument minimum tick (price units) used to snap footprint row sizes.</summary>
    public double InstrumentTick { get; }

    private readonly Contract _contract;
    private int _orderSeq;

    // ── Window sizing derived from options ──────────────────────────────────────────────
    private readonly int _lineWindow;       // bars used by the EW line fits
    private readonly int _kyleWindow;        // bars in the Kyle regression
    private readonly int _covWindow;          // bars logged for Σ / IC
    private readonly int _fwdHorizon;         // forward-return horizon (bars)

    // ── Per-bar accumulation ─────────────────────────────────────────────────────────────
    private readonly List<FootprintPrint> _barPrints = new(2_048);
    private DateTime _barStart = DateTime.MinValue;
    private DateTime _barEnd = DateTime.MinValue;
    private long _cumulativeDelta;

    // ── Bar-mode state (time vs constant-volume) ─────────────────────────────────────────
    private readonly ApexBarMode _barMode;
    private readonly long _volumeBarSize;
    private long _currentBarVolume;   // volume accumulated into the forming bar (volume mode roll)

    // Live price candle (mid-based OHLC), for the price chart only.
    private LiveCandleState? _liveCandle;

    // ── Volume-time (VPIN) state ─────────────────────────────────────────────────────────
    private readonly List<FootprintPrint> _vpinPending = new(4_096);
    private long _bucketVolume = 1;
    private readonly RingBuffer<double> _vpinTau;   // per-bucket toxicity τ
    private readonly RingBuffer<long> _barVolumes = new(50);   // median bar vol estimator

    // ── Tape-speed state (Hawkes self-exciting intensity) ────────────────────────────────
    private readonly TapeArrivalWindow _tape = new();
    private readonly RingBuffer<double> _tapeRateHistory = new(120);   // history of Hawkes intensities
    private readonly HawkesProcess _hawkes;
    private DateTime _hawkesEpoch = DateTime.MinValue;   // reference for seconds conversion

    // ── Predicted node migration (Kalman POC forecasters) ────────────────────────────────
    private readonly KalmanPocPredictor _kalmanBuy;
    private readonly KalmanPocPredictor _kalmanSell;
    private readonly KalmanPocPredictor _kalmanTotal;

    // ── Completed footprint bars (Core type — single source of truth with the chart) ─────
    private const int MaxFootprintBarsExposed = 24;
    private readonly LinkedList<FootprintBar> _footprintBars = new();
    private FootprintBar? _liveFootprint;
    private readonly int _footprintRing;

    // ── Bar-level series for line fits / Kyle / CVD ────────────────────────────────────────
    private readonly RingBuffer<BarFeatures> _barFeatures;

    // ── Depth (OBI) — only when a genuine depth stream exists ─────────────────────────────
    private DepthSnapshot? _latestDepth;
    private DateTime _latestDepthTime = DateTime.MinValue;

    // ── Quote / spread state ───────────────────────────────────────────────────────────────
    private double _lastBid, _lastAsk, _lastMid;
    private double _priorSyntheticTrade;
    private AggressorSide _priorSyntheticClass = AggressorSide.Unknown;
    private bool _sawRealTape;

    // ── Regime ─────────────────────────────────────────────────────────────────────────────
    private string _regime = "Undefined";

    // ── Combination state (logged history) ─────────────────────────────────────────────────
    private readonly RingBuffer<double[]> _scoreHistory;       // per-bar [k] signal scores
    private readonly RingBuffer<double> _midHistory;          // mid at each logged bar
    private double[] _weights;                                  // current Σ⁻¹·IC weights
    private readonly Dictionary<string, double> _weightMap = new();
    private int _barsSinceWeightUpdate;                         // weight-recalc cadence counter

    // ── Calibration state ───────────────────────────────────────────────────────────────────
    private readonly RingBuffer<double> _calibComposites;     // logged C
    private readonly RingBuffer<double> _calibForward;        // realized fwd return at C
    private IsotonicMap _calibration = new(Array.Empty<double>(), Array.Empty<double>(), Array.Empty<int>(), 0);
    private bool _bootstrapMode = true;

    // Conditional slippage: |C| bin → mean observed slippage (price units).
    private const int SlipBins = 8;
    private readonly double[] _slipSum = new double[SlipBins];
    private readonly long[] _slipCount = new long[SlipBins];
    private double _slipFallback;

    // ── Signal TTL stamps ───────────────────────────────────────────────────────────────────
    private readonly Dictionary<string, DateTime> _signalGeneratedAt = new();
    private readonly Dictionary<string, SignalResult> _lastSignal = new();

    // ── Risk / accounting (cost-inclusive) ──────────────────────────────────────────────────
    private double _balance;
    private double _peakEquity;
    private double _dailyPnl;
    private double _sessionPnl;
    private DateTime _lastDay = DateTime.MinValue;
    private DateTime _lastTradeCloseUtc = DateTime.MinValue;
    private bool _killSwitch;

    // ── Position ────────────────────────────────────────────────────────────────────────────
    private long _position;
    private long _pendingFillQty;
    private double _entryPrice;
    private double _stopPrice;
    private double _targetPrice;
    private double _entryComposite;
    private DateTime _entryTimeUtc;
    private double _lastKyleLambda;
    private double _lastSigmaBar = 1e-9;

    // ── Paper trading (simulated OMS — no real orders; this build is data/signals only) ──────
    /// <summary>When false the engine still computes signals/composite but opens no NEW positions;
    /// an existing position keeps managing to its stop/target. Backtests leave this true; the live
    /// host ties it to the Run/arm toggle so paper trades only accrue when armed.</summary>
    public bool PaperTradingEnabled { get; set; } = true;

    private const int MaxTrades = 1_000;
    private readonly List<ApexTradeRecord> _trades = new();

    /// <summary>Completed paper trades in completion order (oldest first), capped at the last
    /// <see cref="MaxTrades"/>.</summary>
    public IReadOnlyList<ApexTradeRecord> Trades => _trades;

    /// <summary>Open-position bracket accessors for the live blotter (stale/0 when flat).</summary>
    public double OpenEntryPrice => _entryPrice;
    public double OpenStopPrice => _stopPrice;
    public double OpenTargetPrice => _targetPrice;
    public double Balance => _balance;

    // ── Snapshot history (UI binding) ───────────────────────────────────────────────────────
    private const int MaxHistory = 500;
    private readonly LinkedList<ApexSnapshotV2> _history = new();

    /// <summary>Most recent v2 snapshot. <c>null</c> until the first tick has been consumed.</summary>
    public ApexSnapshotV2? Latest { get; private set; }

    /// <summary>Snapshots captured per completed bar, oldest first. Capped at <see cref="MaxHistory"/>.</summary>
    public IReadOnlyList<ApexSnapshotV2> History
    {
        get
        {
            var list = new List<ApexSnapshotV2>(_history.Count);
            foreach (var s in _history) list.Add(s);
            return list;
        }
    }

    /// <summary>The strategy's footprint bars for the UI cluster chart, oldest first, ending with
    /// the live forming bar. Built by <see cref="FootprintFeatures.BuildBar"/> — exactly the
    /// per-price aggressor volume the Footprint signal scores.</summary>
    public IReadOnlyList<FootprintBar> FootprintBars
    {
        get
        {
            var take = Math.Min(_footprintBars.Count, MaxFootprintBarsExposed);
            var list = new List<FootprintBar>(take + 1);
            var node = _footprintBars.Last;
            // Walk back `take` then forward to emit oldest → newest.
            for (var i = 0; i < take - 1 && node?.Previous is not null; i++) node = node!.Previous;
            for (; node is not null; node = node.Next) list.Add(node.Value);
            if (_liveFootprint is { } live && live.Rows.Count > 0) list.Add(live);
            return list;
        }
    }

    /// <summary>Snapshot of the engine's in-progress price candle. Null until the first tick.</summary>
    public ApexLiveCandle? LiveCandle
    {
        get
        {
            var c = _liveCandle;
            if (c is null) return null;
            var vol = c.BuyVolume + c.SellVolume;
            var delta = c.BuyVolume - c.SellVolume;
            var eff = vol == 0 ? 0 : (double)Math.Abs(delta) / vol;
            return new ApexLiveCandle(
                c.OpenTime, c.Open, c.High, c.Low, c.Close,
                vol, c.BuyVolume, c.SellVolume, delta, eff);
        }
    }

    public ApexScalperStrategy(Contract contract, ApexV2Options? options = null, TimeSpan? candleInterval = null, double instrumentTick = 0.25)
    {
        _contract = contract;
        Options = options ?? ApexV2Options.Default;
        CandleInterval = candleInterval ?? TimeSpan.FromSeconds(Options.ReferenceSpanSeconds);
        InstrumentTick = instrumentTick > 0 ? instrumentTick : 0.25;

        _barMode = Options.BarMode;
        _volumeBarSize = Math.Max(1, Options.VolumeBarSize);

        _hawkes = new HawkesProcess(Options.HawkesBaselineMu, Options.HawkesAlpha, Options.HawkesBeta);
        _kalmanBuy = new KalmanPocPredictor(Options.KalmanProcessNoise, Options.KalmanMeasurementNoise);
        _kalmanSell = new KalmanPocPredictor(Options.KalmanProcessNoise, Options.KalmanMeasurementNoise);
        _kalmanTotal = new KalmanPocPredictor(Options.KalmanProcessNoise, Options.KalmanMeasurementNoise);

        _lineWindow = Math.Max(8, Options.ForwardReturnHorizon * 4);
        _kyleWindow = Math.Max(8, Options.KyleWindow);
        _covWindow = Math.Max(30, Options.CovarianceWindow);
        _fwdHorizon = Math.Max(1, Options.ForwardReturnHorizon);
        _footprintRing = Math.Max(MaxFootprintBarsExposed, _lineWindow + 2);

        _vpinTau = new RingBuffer<double>(Math.Max(8, Options.VpinLookbackBuckets));
        _barFeatures = new RingBuffer<BarFeatures>(Math.Max(_lineWindow, _kyleWindow) + 4);
        _scoreHistory = new RingBuffer<double[]>(_covWindow + _fwdHorizon + 2);
        _midHistory = new RingBuffer<double>(_covWindow + _fwdHorizon + 2);
        _calibComposites = new RingBuffer<double>(Math.Max(2_000, Options.BootstrapSampleThreshold * 2));
        _calibForward = new RingBuffer<double>(Math.Max(2_000, Options.BootstrapSampleThreshold * 2));

        _weights = new double[SignalNames.Length];
        for (var i = 0; i < _weights.Length; i++) _weights[i] = 1.0 / _weights.Length;

        _balance = 100_000;
        _peakEquity = _balance;
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Pre-seeds the snapshot history with the typical-price line of recent OHLCV bars so the
    /// price chart has context the instant the user clicks Continue. Each seeded snapshot carries
    /// no live signals (all invalid). Safe to call before streaming starts; touches no risk state.
    /// </summary>
    public void SeedFromBars(IReadOnlyList<Bar> bars)
    {
        if (bars.Count == 0) return;
        // Estimate σ_bar and median bar volume from the warm-up bars so distances/thresholds are
        // armed before the first live bar rolls.
        var rets = new List<double>(bars.Count);
        for (var i = 1; i < bars.Count; i++)
            if (bars[i - 1].Close > 0 && bars[i].Close > 0)
                rets.Add(Math.Log(bars[i].Close / bars[i - 1].Close));
        if (rets.Count > 1)
        {
            var m = Mean(rets); var sd = Stdev(rets, m);
            if (sd > 0) _lastSigmaBar = sd;
        }
        foreach (var b in bars) if (b.Volume > 0) _barVolumes.Push(b.Volume);
        if (_barVolumes.Count > 0)
            _bucketVolume = VolumeTimeBucketer.AdaptiveBucketVolume(_barVolumes.ToArray());

        // Seed the bar-feature buffer with synthetic price-structure bars so the line-fit signals
        // (initiative / control / wedge / value) are armed the instant streaming starts, instead of
        // waiting _lineWindow live candles. Historical OHLCV carries no aggressor flow, so we proxy
        // the price-structure fields only: POC ≈ typical price, and the buy/sell volume centroids by
        // the bar's upper/lower half (buyers lift toward the high, sellers hit toward the low). These
        // are honest price levels that track the real recent trend. Flow fields (delta, cumulative
        // delta) are left at zero and these bars are tagged Synthetic so the flow signals skip them.
        var lookback = bars.Count > _barFeatures.Capacity ? bars.Count - _barFeatures.Capacity : 0;
        for (var i = lookback; i < bars.Count; i++)
        {
            var b = bars[i];
            var typical = (b.High + b.Low + b.Close) / 3.0;
            _barFeatures.Push(new BarFeatures(
                b.TimestampUtc, b.Open, b.High, b.Low, b.Close,
                BuyCentroid: (b.High + b.Close) / 2.0,
                SellCentroid: (b.Low + b.Close) / 2.0,
                PocPrice: typical, PocValid: typical > 0 ? 1 : 0,
                Delta: 0, CumulativeDelta: 0, Volume: b.Volume,
                PocOrClose: typical > 0 ? typical : b.Close,
                Synthetic: true));

            // Arm the Kalman POC predictors from the same price-structure proxies so PRED_NODE and the
            // predicted-node exits are available shortly after streaming starts.
            _kalmanBuy.Update((b.High + b.Close) / 2.0);
            _kalmanSell.Update((b.Low + b.Close) / 2.0);
            _kalmanTotal.Update(typical);
        }

        foreach (var b in bars)
        {
            var mid = (b.High + b.Low + b.Close) / 3.0;
            var snap = BuildWarmingSnapshot(b.TimestampUtc, mid);
            _history.AddLast(snap);
            while (_history.Count > MaxHistory) _history.RemoveFirst();
        }
        Latest = _history.Last?.Value;
    }

    /// <summary>Caches the latest order-book snapshot for OBI. Only a genuine depth stream feeds
    /// this; absence/staleness is treated as "no L2" by the OBI signal.</summary>
    public Task OnDepthAsync(DepthSnapshot depth, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (depth.Bids.Count > 0 || depth.Asks.Count > 0)
        {
            _latestDepth = depth;
            _latestDepthTime = depth.TimestampUtc;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Primary input. A real trade print drives every flow signal at full quality. The first real
    /// print flips the engine off the synthetic fallback for good.
    /// </summary>
    public async Task OnTradeAsync(TradePrint trade, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        _sawRealTape = true;
        var aggressor = trade.Aggressor;
        if (aggressor == AggressorSide.Unknown && _lastAsk > _lastBid)
            aggressor = Microstructure.ClassifyAggressor(trade.Price, _lastBid, _lastAsk, _priorSyntheticTrade, _priorSyntheticClass);
        _priorSyntheticTrade = trade.Price;
        if (aggressor != AggressorSide.Unknown) _priorSyntheticClass = aggressor;

        var print = new FootprintPrint(trade.Price, trade.Size, aggressor, trade.EventTimeUtc);
        await IngestPrintAsync(print, trade.Price, FeedQuality.RealTape, router, ct).ConfigureAwait(false);
    }

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        _lastBid = tick.Bid;
        _lastAsk = tick.Ask;
        var mid = (tick.Bid + tick.Ask) * 0.5;
        if (mid > 0) _lastMid = mid;

        // Synthetic fallback: only manufacture pseudo-prints while no real tape has ever arrived.
        if (!_sawRealTape && mid > 0)
        {
            var sizeProxy = Math.Max(1, tick.BidSize + tick.AskSize);
            var side = Microstructure.ClassifyAggressor(mid, tick.Bid, tick.Ask, _priorSyntheticTrade, _priorSyntheticClass);
            _priorSyntheticTrade = mid;
            if (side != AggressorSide.Unknown) _priorSyntheticClass = side;
            var print = new FootprintPrint(mid, sizeProxy, side, tick.TimestampUtc);
            await IngestPrintAsync(print, mid, FeedQuality.SyntheticL1, router, ct).ConfigureAwait(false);
            return;
        }

        // With real tape, quote updates only refresh the price candle / mid and run position
        // management at quote resolution (tighter stop/target evaluation).
        TouchLiveCandle(tick.TimestampUtc, mid);
        UpdateRiskState(tick.TimestampUtc, mid);
        MaintainPosition(tick.TimestampUtc, mid, router, ct);
    }

    /// <summary>The single ingest path shared by real and synthetic prints.</summary>
    private async Task IngestPrintAsync(FootprintPrint print, double refPrice, FeedQuality quality, IOrderRouter router, CancellationToken ct)
    {
        var now = print.TimeUtc;

        // Decide whether the current bar closes BEFORE this print is folded in. Time mode rolls on
        // the clock boundary; volume mode rolls once the forming bar has accumulated VolumeBarSize.
        var rolled = false;
        if (_barStart == DateTime.MinValue)
        {
            StartBar(now);
        }
        else if (_barMode == ApexBarMode.Volume)
        {
            if (_currentBarVolume >= _volumeBarSize) rolled = true;
        }
        else if (TimeBucket(now) >= _barEnd)
        {
            rolled = true;
        }

        if (rolled)
        {
            await CompleteBarAsync(quality, router, ct).ConfigureAwait(false);
            StartBar(now);
            _barPrints.Clear();
        }

        // Accumulate the print into the bar and the volume-time / tape state.
        _barPrints.Add(print);
        _vpinPending.Add(print);
        _currentBarVolume += print.Size;
        if (_barMode == ApexBarMode.Volume) _barEnd = now;   // forming volume bar ends at the latest print
        DrainVolumeBuckets();
        _tape.Add(print);
        FeedHawkes(now);

        // Live forming footprint + price candle for the UI.
        _liveFootprint = FootprintFeatures.BuildBar(
            _barPrints, FootprintTickSize(), _barStart, _barEnd, quality, _cumulativeDelta, ExtractorOptions());
        TouchLiveCandle(now, refPrice, print);

        UpdateRiskState(now, refPrice);
        MaintainPosition(now, refPrice, router, ct);

        // Live snapshot (signals recomputed only on bar close — between bars we refresh the
        // forming snapshot off the latest cached signal results so the dashboard stays live).
        Latest = BuildLiveSnapshot(now, refPrice, quality);
    }

    /// <summary>Opens a fresh bar window. Time mode aligns to the clock bucket; volume mode anchors
    /// to the current print and lets <see cref="_barEnd"/> track the latest print as it fills.</summary>
    private void StartBar(DateTime now)
    {
        if (_barMode == ApexBarMode.Volume)
        {
            _barStart = now;
            _barEnd = now;
        }
        else
        {
            var bucket = TimeBucket(now);
            _barStart = bucket;
            _barEnd = bucket + CandleInterval;
        }
        _currentBarVolume = 0;
    }

    private DateTime TimeBucket(DateTime now) =>
        new(now.Ticks - now.Ticks % CandleInterval.Ticks, DateTimeKind.Utc);

    /// <summary>Registers a print arrival with the Hawkes intensity process (seconds since epoch).</summary>
    private void FeedHawkes(DateTime now)
    {
        if (_hawkesEpoch == DateTime.MinValue) _hawkesEpoch = now;
        _hawkes.Add((now - _hawkesEpoch).TotalSeconds);
    }

    private double HawkesSeconds(DateTime now) =>
        _hawkesEpoch == DateTime.MinValue ? 0 : (now - _hawkesEpoch).TotalSeconds;

    /// <summary>Closes the current bar: builds the footprint via Core, recomputes every signal,
    /// rebuilds weights/calibration, scores the composite, logs history, and (if warm) trades.</summary>
    private async Task CompleteBarAsync(FeedQuality quality, IOrderRouter router, CancellationToken ct)
    {
        var now = _barEnd;
        var bar = FootprintFeatures.BuildBar(
            _barPrints, FootprintTickSize(), _barStart, _barEnd, quality, _cumulativeDelta, ExtractorOptions());
        _cumulativeDelta = bar.CumulativeDelta;

        _footprintBars.AddLast(bar);
        while (_footprintBars.Count > _footprintRing) _footprintBars.RemoveFirst();
        if (bar.TotalVolume > 0) _barVolumes.Push(bar.TotalVolume);
        if (_barVolumes.Count >= 4)
            _bucketVolume = VolumeTimeBucketer.AdaptiveBucketVolume(_barVolumes.ToArray());

        // Bar-level OHLC from the live candle (mid path) for σ / returns.
        var lc = _liveCandle;
        var close = lc?.Close ?? bar.VolumeCentroid;
        var open = lc?.Open ?? close;
        var high = lc?.High ?? close;
        var low = lc?.Low ?? close;
        _barFeatures.Push(new BarFeatures(
            now, open, high, low, close,
            bar.BuyCentroid, bar.SellCentroid, bar.PocPrice, bar.PocPrice <= 0 ? 0 : 1,
            bar.Delta, bar.CumulativeDelta, bar.TotalVolume,
            bar.PocPrice > 0 ? bar.PocPrice : close));

        // Fold the completed bar's volume nodes into the Kalman POC predictors so the next forecast
        // (and the PRED_NODE signal) reflects this bar.
        _kalmanBuy.Update(bar.BuyCentroid);
        _kalmanSell.Update(bar.SellCentroid);
        _kalmanTotal.Update(bar.PocPrice > 0 ? bar.PocPrice : close);

        UpdateSigmaBar();
        UpdateRegime();

        // Compute all signals on the freshly completed bar.
        var sigs = ComputeSignals(now, bar, quality);
        var scores = new double[SignalNames.Length];
        for (var i = 0; i < SignalNames.Length; i++)
        {
            var s = sigs[i];
            _lastSignal[s.Name] = s;
            if (s.IsValid) _signalGeneratedAt[s.Name] = now;
            scores[i] = s.IsValid ? s.Score : double.NaN;
        }

        // Log score + mid for Σ / IC, then refresh weights & calibration.
        _scoreHistory.Push(scores);
        _midHistory.Push(close > 0 ? close : _lastMid);
        // Recompute the Σ⁻¹·IC weights only every WeightRecalcEveryBars bars — they drift slowly over
        // the long covariance window, so this stabilises them and saves the per-bar solve cost.
        if (++_barsSinceWeightUpdate >= Options.WeightRecalcEveryBars)
        {
            UpdateWeights();
            _barsSinceWeightUpdate = 0;
        }
        UpdateCalibration();

        // Hard-clip the composite to [−3, 3] before the isotonic layer so g(C) is always evaluated on
        // the calibrated support (a defensive guard — the weighted blend is already bounded).
        var composite = Math.Clamp(Score(sigs, now, out var compositeDir, out var agree), -3.0, 3.0);
        var gC = EvaluateCalibration(composite);
        var condSlip = ConditionalSlippage(composite);

        var snap = BuildSnapshot(now, close > 0 ? close : _lastMid, sigs, composite, compositeDir, agree, gC, condSlip, quality);
        Latest = snap;
        _history.AddLast(snap);
        while (_history.Count > MaxHistory) _history.RemoveFirst();

        // Trading decision (warm-window gate inside ShouldTrade).
        if (_position == 0 && _pendingFillQty == 0)
            await TryEnterAsync(snap, composite, compositeDir, agree, gC, condSlip, now, router, ct).ConfigureAwait(false);
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct)
    {
        if (evt.State == OrderState.Filled) _pendingFillQty = 0;
        return Task.CompletedTask;
    }

    public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (_position == 0) return;
        var exit = ClosePosition(clock.UtcNow, _lastMid > 0 ? _lastMid : _entryPrice, "SessionEnd");
        if (exit is { } e) await Submit(router, e.Side, e.Qty, ct).ConfigureAwait(false);
    }

    // ── Signal computation ─────────────────────────────────────────────────────────────────
    private SignalResult[] ComputeSignals(DateTime now, FootprintBar bar, FeedQuality quality)
    {
        var q = quality.Multiplier();
        var results = new SignalResult[SignalNames.Length];
        results[0] = CalcDelta(now, q);
        results[1] = CalcVpin(now, q);
        results[2] = CalcFootprint(now, bar, q);
        results[3] = CalcTapeSpeed(now, q);
        var (kyle, lambda) = CalcKyle(now, q);
        _lastKyleLambda = lambda;
        results[4] = kyle;
        var lines = FitLines();
        results[5] = CalcInitiative(now, lines, q);
        results[6] = CalcControl(now, lines, q);
        results[7] = CalcWedge(now, lines, q);
        results[8] = CalcValue(now, lines, q);
        results[9] = CalcCvd(now, q);
        results[10] = CalcObi(now);   // OBI is feed-quality-independent (real depth or invalid)
        results[11] = CalcPredictedNodeMigration(now, lines, q);
        return results;
    }

    /// <summary>The bar-feature series with the warm-up-seeded (<see cref="BarFeatures.Synthetic"/>)
    /// bars stripped out. Flow signals (delta / Kyle / CVD) score off this so they never read the
    /// zero-flow proxy bars; the line-fit signals use the full buffer (their price-structure proxy is
    /// honest). Seeded bars are always the oldest entries, so this is just the live suffix.</summary>
    private BarFeatures[] LiveBarFeatures()
    {
        var all = _barFeatures.ToArray();
        var live = 0;
        for (var i = 0; i < all.Length; i++) if (!all[i].Synthetic) live++;
        if (live == all.Length) return all;
        var result = new BarFeatures[live];
        Array.Copy(all, all.Length - live, result, 0, live);
        return result;
    }

    // ── Signal: Delta ──────────────────────────────────────────────────────────────────────
    private SignalResult CalcDelta(DateTime now, double q)
    {
        var feats = LiveBarFeatures();   // oldest → newest, live bars only
        var n = feats.Length;
        var accelP = Math.Max(1, Math.Min(3, n / 4));
        if (n < Math.Max(6, accelP + 2)) return SignalResult.Invalid(SigDelta, now);
        var deltas = new double[n];
        for (var i = 0; i < n; i++) deltas[i] = feats[i].Delta;

        var mD = Mean(deltas); var sdD = Stdev(deltas, mD);
        var z = ZScore(deltas[n - 1], mD, sdD);
        var theta = DeltaZThreshold();
        var scoreA = ClampScore(z / theta * 3.0);

        var accelN = n - accelP;
        var accels = new double[accelN];
        for (var i = 0; i < accelN; i++) accels[i] = deltas[i + accelP] - deltas[i];
        var mA = Mean(accels); var sdA = Stdev(accels, mA);
        var zA = ZScore(accels[accelN - 1], mA, sdA);
        var scoreB = ClampScore(zA / theta * 3.0);

        var score = scoreA * 0.6 + scoreB * 0.4;
        var conf = q * Math.Clamp((Math.Abs(z) + Math.Abs(zA)) / (theta * 4.0), 0, 1);
        return SignalResult.From(SigDelta, score, conf, now);
    }

    // ── Signal: VPIN ──────────────────────────────────────────────────────────────────────
    private SignalResult CalcVpin(DateTime now, double q)
    {
        if (_vpinTau.Count < 2) return SignalResult.Invalid(SigVpin, now);
        var taus = _vpinTau.ToArray();
        var look = Math.Min(Options.VpinLookbackBuckets, taus.Length);
        double sum = 0;
        for (var i = taus.Length - look; i < taus.Length; i++) sum += taus[i];
        var vpin = sum / look;

        // Direction from the most-recent bucket's buy fraction (τ encodes magnitude, not sign).
        var lastBucketBuyHeavy = _lastBucketBuyFraction > 0.5;
        // Score scales with toxicity; sign follows the net flow direction.
        var mag = Math.Clamp(vpin * 3.0, 0, 3.0);
        var score = lastBucketBuyHeavy ? mag : -mag;
        var conf = q * Math.Clamp(vpin, 0, 1);
        return SignalResult.From(SigVpin, score, conf, now);
    }

    private double _lastBucketBuyFraction = 0.5;

    private void DrainVolumeBuckets()
    {
        if (_bucketVolume <= 0) return;
        // Bucketize the pending prints; on each completed bucket push τ and reset.
        long total = 0;
        foreach (var p in _vpinPending) total += p.Size;
        if (total < _bucketVolume) return;

        foreach (var b in VolumeTimeBucketer.Bucketize(_vpinPending, _bucketVolume))
        {
            _vpinTau.Push(2.0 * Math.Abs(b.BuyFraction - 0.5));
            _lastBucketBuyFraction = b.BuyFraction;
        }
        // Keep only the trailing partial (volume below one bucket) for the next drain: walk from
        // the start summing into whole buckets; the remainder after the last boundary is retained.
        long acc = 0; var lastBoundary = 0;
        for (var i = 0; i < _vpinPending.Count; i++)
        {
            acc += _vpinPending[i].Size;
            if (acc >= _bucketVolume) { acc = 0; lastBoundary = i + 1; }
        }
        if (lastBoundary > 0) _vpinPending.RemoveRange(0, lastBoundary);
    }

    // ── Signal: footprint stacked imbalance ─────────────────────────────────────────────────
    private SignalResult CalcFootprint(DateTime now, FootprintBar bar, double q)
    {
        var bars = _footprintBars;
        if (bars.Count == 0) return SignalResult.Invalid(SigFootprint, now);

        // Average the stacked-imbalance contrast over the last few completed bars.
        var take = Math.Min(3, bars.Count);
        double sum = 0;
        var node = bars.Last;
        for (var i = 0; i < take && node is not null; i++, node = node.Previous)
        {
            var b = node.Value;
            var bull = Math.Clamp(b.StackedBuy, 0, 3);
            var bear = Math.Clamp(b.StackedSell, 0, 3);
            sum += bull - bear;
        }
        var score = ClampScore(sum / take);
        // Nudge from the forming bar's leading stacks.
        if (_liveFootprint is { } live)
        {
            if (live.StackedBuy >= 2 && score >= 0) score = ClampScore(score + 0.5);
            if (live.StackedSell >= 2 && score <= 0) score = ClampScore(score - 0.5);
        }
        var conf = q * Math.Abs(score) / 3.0;
        return SignalResult.From(SigFootprint, score, conf, now);
    }

    // ── Signal: tape speed ──────────────────────────────────────────────────────────────────
    private SignalResult CalcTapeSpeed(DateTime now, double q)
    {
        var windowSec = Math.Max(1.0, CandleInterval.TotalSeconds * Options.TtlMultipliers.ObiTapeSpeed);
        // Hawkes self-exciting intensity at `now` — a sharper "tape speed" than a flat arrival count:
        // a burst of trades spikes it via self-excitation, and it decays exponentially as the sweep
        // cools, so it leads the simple rolling rate at both the onset and the fade of aggression.
        var intensity = _hawkes.Intensity(HawkesSeconds(now));
        _tapeRateHistory.Push(intensity);
        if (_tapeRateHistory.Count < 20) return SignalResult.Invalid(SigTapeSpeed, now);

        var hist = _tapeRateHistory.ToArray();
        var m = Mean(hist); var sd = Stdev(hist, m);
        var z = ZScore(intensity, m, sd);
        var upFrac = _tape.UpTickFraction(now, windowSec);

        double score = 0;
        var conf = q * Math.Clamp(Math.Abs(z) / 4.0, 0, 1);
        if (z > 2.0)
        {
            if (upFrac > 0.65) score = ClampScore(z / 2.0 * 3.0);
            else if (upFrac < 0.35) score = -ClampScore(z / 2.0 * 3.0);
            else { score = 0; conf = q * 0.1; }
        }
        return SignalResult.From(SigTapeSpeed, score, conf, now);
    }

    // ── Signal: Kyle-λ residual ─────────────────────────────────────────────────────────────
    private (SignalResult, double Lambda) CalcKyle(DateTime now, double q)
    {
        var feats = LiveBarFeatures();
        var n = feats.Length;
        var win = Math.Min(_kyleWindow, n);
        if (win < 6) return (SignalResult.Invalid(SigKyle, now), 0);

        var rets = new double[win];
        var flow = new double[win];
        for (var i = 0; i < win; i++)
        {
            var idx = n - win + i;
            var prev = idx > 0 ? feats[idx - 1].Close : feats[idx].Close;
            rets[i] = prev > 0 && feats[idx].Close > 0 ? Math.Log(feats[idx].Close / prev) : 0;
            flow[i] = feats[idx].Delta;
        }
        var fit = KyleResidual.Fit(rets, flow);
        var z = fit.CumulativeResidualZ;
        // ε_cum ≫ 0 → fragile rally (price ran above flow) → fade (negative score).
        // ε_cum ≪ 0 with positive delta → absorption (buyers absorbed) → positive bias.
        var score = ClampScore(-z);
        if (z < 0 && feats[n - 1].Delta > 0) score = ClampScore(Math.Abs(z));   // absorption long bias
        // Down-weight confidence when the 2SLS instrument is weak (lagged flow barely predicts
        // current flow ⇒ λ̂ is poorly identified). A weak instrument halves conf rather than killing it.
        var conf = q * Math.Clamp(fit.RSquared, 0, 1) * (0.5 + 0.5 * Math.Clamp(fit.FirstStageRSquared, 0, 1));
        return (SignalResult.From(SigKyle, score, conf, now), fit.Lambda);
    }

    // ── Triple line block ───────────────────────────────────────────────────────────────────
    private LineTriple FitLines()
    {
        var n = _barFeatures.Count;
        var win = Math.Min(_lineWindow, n);
        if (win < 4) return LineTriple.Empty;

        var feats = _barFeatures.ToArray();
        var x = new double[win];
        var buy = new double[win];
        var sell = new double[win];
        var poc = new double[win];
        var w = new double[win];
        for (var i = 0; i < win; i++)
        {
            var idx = n - win + i;
            x[i] = i;
            buy[i] = feats[idx].BuyCentroid;
            sell[i] = feats[idx].SellCentroid;
            poc[i] = feats[idx].PocPrice;
            w[i] = Math.Max(1.0, feats[idx].Volume);
        }

        var buyFit = FitLine(x, buy, w);
        var sellFit = FitLine(x, sell, w);
        var pocFit = FitLine(x, poc, w);
        return new LineTriple(buyFit, sellFit, pocFit, x[win - 1]);
    }

    private ApexLineFit FitLine(double[] x, double[] y, double[] w)
    {
        var fit = EwRegression.Fit(x, y, Options.EwDelta, w);
        var resid = EwRegression.Residuals(x, y, fit);
        var nw = NeweyWest.SlopeStandardError(x, resid, fit.Slope, Options.NeweyWestLag ?? -1);
        var endpoint = fit.Predict(x[^1]);
        return new ApexLineFit(fit.Slope, fit.Intercept, endpoint, fit.RSquared, nw.StandardError, fit.ResidualStdev);
    }

    // (b.1) initiative contrast t = (β_b − β_s) / se_NW
    private SignalResult CalcInitiative(DateTime now, LineTriple l, double q)
    {
        if (!l.Valid) return SignalResult.Invalid(SigInitiative, now);
        var seB = l.Buy.NeweyWestStandardError;
        var seS = l.Sell.NeweyWestStandardError;
        var se = Math.Sqrt(seB * seB + seS * seS);
        if (se <= 1e-12) return SignalResult.Invalid(SigInitiative, now);
        var t = (l.Buy.Slope - l.Sell.Slope) / se;
        var score = ClampScore(t);   // t already dimensionless; clamp to [-3,3]
        var r2 = Math.Min(l.Buy.RSquared, l.Sell.RSquared);
        var conf = q * Math.Clamp(r2, 0, 1);
        return SignalResult.From(SigInitiative, score, conf, now);
    }

    // (b.2/3) control coordinate ρ and rotation dρ/dt
    private double _prevRho = double.NaN;
    private SignalResult CalcControl(DateTime now, LineTriple l, double q)
    {
        if (!l.Valid) return SignalResult.Invalid(SigControl, now);
        var b = l.Buy.FittedEndpoint;
        var s = l.Sell.FittedEndpoint;
        var p = l.Poc.FittedEndpoint;
        var span = b - s;
        if (Math.Abs(span) <= 1e-12) return SignalResult.Invalid(SigControl, now);
        var rho = Math.Clamp((p - s) / span, 0, 1);

        // dρ/dt: rho moving toward 1 (buyers) ⇒ bullish; toward 0 ⇒ bearish.
        var drho = double.IsNaN(_prevRho) ? 0 : rho - _prevRho;
        _prevRho = rho;
        _lastControlVelocity = drho;

        // Score: center ρ on 0.5, scale to [-3,3], reinforce with rotation.
        var score = ClampScore((rho - 0.5) * 6.0 + Math.Sign(drho) * Math.Min(1.0, Math.Abs(drho) * 30.0));
        var r2 = Math.Min(l.Poc.RSquared, Math.Min(l.Buy.RSquared, l.Sell.RSquared));
        var conf = q * Math.Clamp(r2, 0, 1);
        return SignalResult.From(SigControl, score, conf, now);
    }

    // (b.4) wedge dynamics w = b̄ − s̄, dw/dt = β_b − β_s
    private SignalResult CalcWedge(DateTime now, LineTriple l, double q)
    {
        if (!l.Valid) return SignalResult.Invalid(SigWedge, now);
        var dw = l.Buy.Slope - l.Sell.Slope;
        var converging = dw < 0;   // wedge narrowing — a coil; resolve with sign(β_poc)
        double score;
        if (converging)
            score = ClampScore(Math.Sign(l.Poc.Slope) * Math.Min(3.0, Math.Abs(l.Poc.SlopeTStat)));
        else
            // expansion: direction follows which side is advancing faster
            score = ClampScore((l.Buy.Slope - l.Sell.Slope) / Math.Max(l.Poc.ResidualStdev, 1e-9));
        var r2 = Math.Min(l.Buy.RSquared, l.Sell.RSquared);
        var conf = q * Math.Clamp(r2, 0, 1);
        return SignalResult.From(SigWedge, score, conf, now);
    }

    // (b.5) value deviation z_p = (price − p̂) / σ_res
    private SignalResult CalcValue(DateTime now, LineTriple l, double q)
    {
        if (!l.Valid) return SignalResult.Invalid(SigValue, now);
        var sigma = l.Poc.ResidualStdev;
        if (sigma <= 1e-12) return SignalResult.Invalid(SigValue, now);
        var zp = (_lastMid - l.Poc.FittedEndpoint) / sigma;

        // Fade bias when |z_p| > ~2 and the POC trend is flat; do NOT fade when β_poc is steep
        // and ρ rising (handled by zeroing the fade when the POC t-stat is large).
        var pocFlat = Math.Abs(l.Poc.SlopeTStat) < 1.5;
        double score = 0;
        if (Math.Abs(zp) > 2.0 && pocFlat) score = ClampScore(-zp);   // fade the stretch toward value
        var conf = q * Math.Clamp(l.Poc.RSquared, 0, 1) * Math.Clamp(Math.Abs(zp) / 4.0, 0, 1);
        return SignalResult.From(SigValue, score, conf, now);
    }

    // (c) CVD divergence
    private SignalResult CalcCvd(DateTime now, double q)
    {
        var feats = LiveBarFeatures();
        var n = feats.Length;
        var win = Math.Min(_lineWindow, n);
        if (win < 4) return SignalResult.Invalid(SigCvd, now);
        var x = new double[win]; var price = new double[win]; var cvd = new double[win];
        for (var i = 0; i < win; i++)
        {
            var idx = n - win + i;
            x[i] = i; price[i] = feats[idx].Close; cvd[i] = feats[idx].CumulativeDelta;
        }
        var pFit = EwRegression.Fit(x, price, Options.EwDelta);
        var cFit = EwRegression.Fit(x, cvd, Options.EwDelta);
        var sp = Math.Sign(pFit.Slope);
        var sc = Math.Sign(cFit.Slope);
        if (sp == 0 || sc == 0 || sp == sc) return SignalResult.From(SigCvd, 0, 0, now);
        // Divergence: price rising while CVD falling ⇒ fade the price (S = −3·sign(β_p)·minR²).
        var minR2 = Math.Min(pFit.RSquared, cFit.RSquared);
        var score = ClampScore(-3.0 * sp * minR2);
        var conf = q * Math.Clamp(minR2, 0, 1);
        return SignalResult.From(SigCvd, score, conf, now);
    }

    // ── Signal: OBI (real depth only) ─────────────────────────────────────────────────────
    private SignalResult CalcObi(DateTime now)
    {
        var depth = _latestDepth;
        if (depth is null) return SignalResult.Invalid(SigObi, now);
        // Staleness: depth older than the OBI TTL is treated as no-L2.
        var ttl = TtlMsFor(SigObi);
        if ((now - _latestDepthTime).TotalMilliseconds > ttl) return SignalResult.Invalid(SigObi, now);
        if (depth.Bids.Count == 0 || depth.Asks.Count == 0) return SignalResult.Invalid(SigObi, now);

        var bid = (double)depth.BestBidSize;
        var ask = (double)depth.BestAskSize;
        var total = bid + ask;
        if (total <= 0) return SignalResult.Invalid(SigObi, now);
        var obi = (bid - ask) / total;
        // OBI from genuine tape carries full weight (q applies to flow signals, not the book).
        return SignalResult.From(SigObi, ClampScore(obi * 3.0), Math.Abs(obi), now);
    }

    // ── Signal: predicted node migration (Kalman POC forecast) ───────────────────────────────
    /// <summary>
    /// The 12th signal. Forecasts the buy/sell/total POC <c>n = PredictedNodeHorizon</c> bars ahead
    /// with the Kalman predictors and reads the predicted wedge dynamics:
    /// <list type="bullet">
    ///   <item>expanding wedge ⇒ a developing trend → score = +3·sgn(v̂_total) (follow the predicted
    ///   total-POC drift);</item>
    ///   <item>converging wedge (a coil) ⇒ fade the current stretch → score = −3·sgn(z_p).</item>
    /// </list>
    /// Everything is scaled by the prediction confidence 1 − σ²_pred/σ²_bar, so a noisy forecast
    /// contributes little.
    /// </summary>
    private SignalResult CalcPredictedNodeMigration(DateTime now, LineTriple lines, double q)
    {
        var pred = PredictNodes();
        if (pred.Conf <= 0) return SignalResult.Invalid(SigPredNode, now);

        // Current wedge from the live Kalman states; predicted wedge from the forecast.
        var curWedge = Math.Abs(_kalmanBuy.Price - _kalmanSell.Price);
        var predWedge = Math.Abs(pred.Wedge);
        var expanding = predWedge > curWedge;

        double score;
        if (expanding)
        {
            // Trend developing: direction follows the predicted total-POC drift.
            score = ClampScore(3.0 * Math.Sign(_kalmanTotal.Velocity) * pred.Conf);
        }
        else
        {
            // Coiling: fade the current deviation of price from fitted value.
            var (zp, _) = LastValueDeviation(lines);
            score = ClampScore(-3.0 * Math.Sign(zp) * pred.Conf);
        }
        return SignalResult.From(SigPredNode, score, q * pred.Conf, now);
    }

    /// <summary>
    /// Kalman forecast of the buy/sell/total POC <c>n = PredictedNodeHorizon</c> bars ahead, plus the
    /// predicted wedge Ŵ = P̂_buy − P̂_sell and a confidence ∈ [0, 1] = 1 − σ²_pred/σ²_bar (clamped).
    /// Confidence is 0 until the predictors are initialised, or when the forecast variance of the
    /// total POC exceeds the bar's diffusion variance over the same horizon.
    /// </summary>
    private (double Buy, double Sell, double Total, double Wedge, double Conf, int Horizon) PredictNodes()
    {
        var n = Math.Max(1, Options.PredictedNodeHorizon);
        if (!_kalmanTotal.IsInitialized || !_kalmanBuy.IsInitialized || !_kalmanSell.IsInitialized)
            return (0, 0, 0, 0, 0, n);

        var (pBuy, _) = _kalmanBuy.Forecast(n);
        var (pSell, _) = _kalmanSell.Forecast(n);
        var (pTot, vTot) = _kalmanTotal.Forecast(n);

        // σ²_bar over n bars in price² (Brownian: variance grows linearly in time).
        var sigmaBarPrice = _lastSigmaBar * (_lastMid > 0 ? _lastMid : 1.0);
        var barVar = sigmaBarPrice * sigmaBarPrice * n;
        var conf = barVar > 1e-12 ? Math.Clamp(1.0 - vTot / barVar, 0.0, 1.0) : 0.0;
        return (pBuy, pSell, pTot, pBuy - pSell, conf, n);
    }

    // ── Combination ─────────────────────────────────────────────────────────────────────────
    private void UpdateWeights()
    {
        var k = SignalNames.Length;
        var rows = _scoreHistory.ToArray();
        var mids = _midHistory.ToArray();
        // Need scores + a forward return horizon ahead.
        if (rows.Length < Math.Max(20, _fwdHorizon + 5)) return;

        var usable = rows.Length - _fwdHorizon;
        if (usable < 15) return;

        // Forward return aligned to each scored bar.
        var fwd = new double[usable];
        for (var i = 0; i < usable; i++)
        {
            var p0 = mids[i]; var p1 = mids[i + _fwdHorizon];
            fwd[i] = p0 > 0 && p1 > 0 ? Math.Log(p1 / p0) : 0;
        }

        // Score matrix (usable × k); NaN-fill stale entries with 0 for covariance stability.
        var mat = new double[usable, k];
        for (var i = 0; i < usable; i++)
            for (var j = 0; j < k; j++)
            {
                var v = rows[i][j];
                mat[i, j] = double.IsNaN(v) ? 0.0 : v;
            }

        try
        {
            var lw = LedoitWolf.Estimate(mat);
            var ic = InformationCoefficient.Compute(mat, fwd);
            var icVec = new double[k];
            for (var j = 0; j < k; j++) icVec[j] = ic[j].Spearman;   // rank IC — robust
            _weights = SignalWeights.Solve(lw.Covariance, icVec);
        }
        catch
        {
            // Degenerate window — keep prior weights.
        }
    }

    private void UpdateCalibration()
    {
        // On each completed bar, when a forward-return target is now available for the bar
        // _fwdHorizon ago, log (composite_then, realized_fwd) into the calibration set.
        var rows = _scoreHistory.ToArray();
        var mids = _midHistory.ToArray();
        if (rows.Length <= _fwdHorizon) return;

        var i = rows.Length - 1 - _fwdHorizon;
        if (i < 0) return;
        var c = CompositeFromScores(rows[i]);
        var p0 = mids[i]; var p1 = mids[^1];
        if (p0 > 0 && p1 > 0)
        {
            _calibComposites.Push(c);
            _calibForward.Push(Math.Log(p1 / p0));
        }

        var comps = _calibComposites.ToArray();
        var fwds = _calibForward.ToArray();
        if (comps.Length < 10) { _bootstrapMode = true; return; }

        _calibration = comps.Length >= Options.IsotonicMinSamples
            ? IsotonicCalibration.Fit(comps, fwds, Options.IsotonicMinSamples)
            : IsotonicCalibration.BinnedMean(comps, fwds, Math.Max(3, comps.Length / 5));

        // Bootstrap when total samples or per-region counts are too low to trust.
        var thin = comps.Length < Options.BootstrapSampleThreshold;
        var sparseRegion = _calibration.Counts.Length == 0 || _calibration.Counts.Min() < Math.Max(3, Options.IsotonicMinSamples / 10);
        _bootstrapMode = thin || sparseRegion;
    }

    private double EvaluateCalibration(double composite)
    {
        if (_calibration.X.Length == 0) return 0;
        var g = _calibration.Evaluate(composite);
        if (_bootstrapMode) g *= 0.5;   // blend toward neutral while unproven
        return g;
    }

    private double CompositeFromScores(double[] scores)
    {
        double num = 0, den = 0;
        for (var i = 0; i < SignalNames.Length; i++)
        {
            var s = scores[i];
            if (double.IsNaN(s)) continue;
            var w = _weights[i];
            var m = RegimeMultiplier(SignalNames[i]);
            num += w * m * s;
            den += Math.Abs(w * m);
        }
        return den > 1e-10 ? num / den : 0;
    }

    private double Score(IReadOnlyList<SignalResult> sigs, DateTime now, out int direction, out int agree)
    {
        double num = 0, den = 0;
        agree = 0;
        var dir0 = 0;
        // First pass for the composite.
        for (var i = 0; i < sigs.Count; i++)
        {
            var r = sigs[i];
            if (!r.IsValid || IsStale(r.Name, now)) continue;
            var w = _weights[i];
            var m = RegimeMultiplier(r.Name);
            num += w * m * r.Confidence * r.Score;
            den += Math.Abs(w * m);
        }
        var composite = den > 1e-10 ? num / den : 0;
        dir0 = composite > 0.05 ? 1 : composite < -0.05 ? -1 : 0;
        direction = dir0;

        // Agreement count (soft sanity gate): strong valid signals pointing the composite's way.
        for (var i = 0; i < sigs.Count; i++)
        {
            var r = sigs[i];
            if (!r.IsValid || IsStale(r.Name, now) || Math.Abs(r.Score) < 0.5) continue;
            if (r.Direction == dir0 && dir0 != 0) agree++;
        }
        return composite;
    }

    private double RegimeMultiplier(string name)
    {
        var baseMult = Options.RegimeMultipliers.TryGetValue(_regime, out var m) ? m : 1.0;
        // Augment with flow-based regime (wedge dynamics): in a coil, favor structural lines;
        // in expansion, favor momentum/delta. Light touch so it nudges rather than dominates.
        var wedge = _lastSignal.TryGetValue(SigWedge, out var ws) && ws.IsValid ? ws.Score : 0;
        var coil = wedge != 0 && Math.Abs(wedge) < 1.0;   // weak wedge ⇒ coiling
        var flowMult = name switch
        {
            SigInitiative or SigControl or SigWedge or SigValue or SigPredNode => coil ? 1.15 : 1.0,
            SigDelta or SigTapeSpeed => coil ? 0.9 : 1.1,
            _ => 1.0,
        };
        return baseMult * flowMult;
    }

    private bool IsStale(string name, DateTime now)
    {
        if (!_signalGeneratedAt.TryGetValue(name, out var ts)) return true;
        return (now - ts).TotalMilliseconds >= TtlMsFor(name);
    }

    private double TtlMsFor(string name)
    {
        var span = CandleInterval.TotalMilliseconds;
        var t = Options.TtlMultipliers;
        var alpha = name switch
        {
            SigDelta or SigFootprint => t.DeltaFootprint,
            SigObi or SigTapeSpeed => t.ObiTapeSpeed,
            SigInitiative or SigControl or SigWedge or SigValue or SigCvd or SigPredNode => t.PocLines,
            SigVpin => t.DeltaFootprint,
            SigKyle => t.PocLines,
            _ => 1.0,
        };
        return alpha * span;
    }

    // ── Entry ───────────────────────────────────────────────────────────────────────────────
    private async Task TryEnterAsync(ApexSnapshotV2 snap, double composite, int direction, int agree,
        double gC, double condSlip, DateTime now, IOrderRouter router, CancellationToken ct)
    {
        if (_killSwitch) return;
        if (!PaperTradingEnabled) return;   // disarmed: compute signals, open no new positions
        if (direction == 0) return;
        if (!_sawRealTape && Options.AbsorptionVolumeFraction >= 0)
        {
            // Synthetic-only: refuse to arm unless explicitly running heavily discounted. We do
            // run, but the q multiplier on every confidence already collapses the composite; the
            // calibration gate below then almost always rejects. Surfaced via snapshot.FeedQuality.
        }
        // Warm window: count LIVE bars only. The synthetic warm-up seed arms the line-fit signals
        // (so the composite/gauge update immediately), but order arming must wait for a full window
        // of genuine live tape — never trade off the proxy-seeded structure.
        if (LiveBarFeatures().Length < _lineWindow) return;
        if (!IsSessionAllowed(now)) return;

        var secondsSinceClose = (now - _lastTradeCloseUtc).TotalSeconds;
        if (_lastTradeCloseUtc != DateTime.MinValue && secondsSinceClose < Options.CooldownSeconds) return;

        // Bootstrap gate: until the isotonic curve is trusted, fall back to a fixed composite
        // threshold; once trusted, the cost gate below is authoritative.
        if (_bootstrapMode && Math.Abs(composite) < Options.CompositeThreshold) return;

        // Soft min-agreement sanity gate.
        if (agree < 2) return;

        var entry = direction > 0 ? _lastAsk : _lastBid;
        if (entry <= 0) entry = _lastMid;
        if (entry <= 0) return;

        // Cost gate: g(C) must clear spread + 2·fee + E[slip|C] in the composite's direction.
        var tick = InstrumentTick;
        var spreadCost = Math.Max(_lastAsk - _lastBid, Options.SpreadCostTicks * tick);
        var fee = Options.CommissionPerSide;

        // Adaptive slippage: the conditional model, surcharged by immediate book pressure on the side
        // we are trading INTO (the depth resisting our fill). slip = condSlip · (1 + oppositeOBI).
        var oppObi = OppositeBookPressure(direction, now);
        var adaptiveSlip = condSlip * (1.0 + oppObi);

        var requiredEdge = spreadCost + 2.0 * fee + adaptiveSlip;
        // g(C) is a return; convert to price units around entry for comparison.
        var edgePrice = Math.Abs(gC) * entry;
        if (direction > 0 && gC <= 0) return;
        if (direction < 0 && gC >= 0) return;
        if (edgePrice < requiredEdge) return;

        // Structure / predicted-node bracket.
        var (stop, target) = ComputeBracket(direction, entry);
        if (stop <= 0 || target <= 0) return;
        var stopDist = Math.Abs(entry - stop);
        var targetDist = Math.Abs(target - entry);
        if (stopDist <= 0 || targetDist <= 0) return;

        // First-passage EV sanity: μ = g(C) drift over a bar, σ from bar vol, penalised for jump/gap
        // risk (the empirical frequency of recent bars large enough to span the nearer barrier — a
        // discontinuity the continuous-path formula can't see).
        var mu = gC * entry;   // expected price move per bar
        var sigma = _lastSigmaBar * entry;
        var gap = GapPenalty(Math.Min(stopDist, targetDist));
        var p = FirstPassage.WinProbability(stopDist, targetDist, direction > 0 ? mu : -mu, sigma, gap);
        var roundTrip = spreadCost + 2.0 * fee + adaptiveSlip;
        var ev = FirstPassage.ExpectedValue(p, targetDist, stopDist, roundTrip);
        if (ev <= 0) return;

        var qty = SizePosition(stopDist, gC, entry, SessionKellyCap(now));
        if (qty <= 0) return;

        _entryPrice = entry;
        _stopPrice = stop;
        _targetPrice = target;
        _entryComposite = composite;
        _entryTimeUtc = now;
        _pendingFillQty = qty;
        _position = direction > 0 ? qty : -qty;

        await Submit(router, direction > 0 ? OrderSide.Buy : OrderSide.Sell, qty, ct).ConfigureAwait(false);
    }

    // ── Exits / brackets ──────────────────────────────────────────────────────────────────────
    private (double Stop, double Target) ComputeBracket(int direction, double entry)
    {
        var sigmaPrice = _lastSigmaBar * entry;
        var span = CandleInterval.TotalSeconds;
        var scale = Math.Sqrt(Math.Max(span, 1e-9) / Math.Max(Options.ReferenceSpanSeconds, 1e-9));
        var stopFixed = Options.StopSigmaCoefficient * sigmaPrice * scale;
        var targetFixed = Options.TargetSigmaCoefficient * sigmaPrice * scale;
        var buffer = Math.Max(InstrumentTick, 0.25 * stopFixed);

        // ── Preferred: predicted-node exits when the Kalman forecast is confident enough ──────────
        // TP = predicted buy POC (long) / sell POC (short); SL = predicted sell POC (long) / buy POC
        // (short). Only used when both levels straddle entry on the correct sides; otherwise we fall
        // back to the structure/ATR logic below (e.g. when the forecast variance is too high).
        var pred = PredictNodes();
        if (pred.Conf >= Options.PredictionExitMinConfidence)
        {
            double pStop = 0, pTarget = 0;
            if (direction > 0)
            {
                if (pred.Buy > entry) pTarget = pred.Buy;
                if (pred.Sell > 0 && pred.Sell < entry) pStop = pred.Sell - buffer;
            }
            else
            {
                if (pred.Sell > 0 && pred.Sell < entry) pTarget = pred.Sell;
                if (pred.Buy > entry) pStop = pred.Buy + buffer;
            }
            if (pStop > 0 && pTarget > 0) return (pStop, pTarget);
        }

        // Flow-structure anchors: wedge boundaries (buy/sell fitted lines) and value-area edges.
        var lines = FitLines();
        double stop = 0, target = 0;
        if (lines.Valid)
        {
            var b = lines.Buy.FittedEndpoint;
            var s = lines.Sell.FittedEndpoint;
            var p = lines.Poc.FittedEndpoint;
            var va = Options.ValueAreaSigmaCoefficient * Math.Max(lines.Poc.ResidualStdev, sigmaPrice * scale);
            if (direction > 0)
            {
                // Stop beneath sell line / lower value edge; target at buy line / upper value edge.
                var anchor = Math.Min(s, p - va) - buffer;
                if (anchor > 0 && entry - anchor <= 3.0 * stopFixed && anchor < entry) stop = anchor;
                var tAnchor = Math.Max(b, p + va);
                if (tAnchor > entry) target = tAnchor;
            }
            else
            {
                var anchor = Math.Max(b, p + va) + buffer;
                if (anchor > entry && anchor - entry <= 3.0 * stopFixed) stop = anchor;
                var tAnchor = Math.Min(s, p - va);
                if (tAnchor > 0 && tAnchor < entry) target = tAnchor;
            }
        }

        // Fall back to fixed RR when no valid structure within range.
        if (stop <= 0) stop = direction > 0 ? entry - stopFixed : entry + stopFixed;
        if (target <= 0)
        {
            var sd = Math.Abs(entry - stop);
            target = direction > 0 ? entry + sd * 1.5 : entry - sd * 1.5;
        }
        return (stop, target);
    }

    private long SizePosition(double stopDist, double gC, double entry, double kellyCap)
    {
        if (stopDist <= 0) return 0;
        // Quarter-Kelly (session-capped) on the conditional edge. Kelly fraction f* ≈ edge / odds for
        // a bracket; approximate with the calibrated expected return relative to the stop risk.
        var edge = Math.Abs(gC) * entry;        // expected gain (price units)
        var kelly = edge / Math.Max(stopDist, 1e-9);
        var capped = Math.Clamp(kelly, 0, 1) * kellyCap;

        // Risk-fraction sizing: qty such that stopDist·qty ≈ riskCash, then cap at Kelly.
        var riskCash = _balance * Options.RiskFraction;
        var byRisk = riskCash / stopDist;
        var byKelly = (_balance * capped) / stopDist;
        var qty = Math.Min(byRisk, byKelly);

        // Scale DOWN when λ̂ is elevated (thin book ⇒ higher impact / risk).
        var lambdaScale = LambdaSizeScale();
        qty *= lambdaScale;

        return Math.Max(0, (long)Math.Floor(qty));
    }

    private double LambdaSizeScale()
    {
        // Compare current λ̂ to its recent magnitude; high λ ⇒ scale toward 0.5.
        var lam = Math.Abs(_lastKyleLambda);
        if (lam <= 0) return 1.0;
        // Soft logistic-ish damp: never below 0.5, never above 1.0.
        var norm = Math.Clamp(lam / (lam + _lastSigmaBar + 1e-9), 0, 1);
        return Math.Clamp(1.0 - 0.5 * norm, 0.5, 1.0);
    }

    private void MaintainPosition(DateTime now, double price, IOrderRouter router, CancellationToken ct)
    {
        if (_position == 0 || price <= 0) return;

        // Time-stop: max hold as a multiple of bar span (use PocLines α as the hold budget).
        var maxHoldMs = Options.TtlMultipliers.PocLines * CandleInterval.TotalMilliseconds * 4.0;
        var timeStop = _entryTimeUtc != DateTime.MinValue && (now - _entryTimeUtc).TotalMilliseconds > maxHoldMs;

        var hitStop = (_position > 0 && price <= _stopPrice) || (_position < 0 && price >= _stopPrice);
        var hitTarget = (_position > 0 && price >= _targetPrice) || (_position < 0 && price <= _targetPrice);
        if (!hitStop && !hitTarget && !timeStop) return;

        var reason = hitTarget ? "Target" : hitStop ? "Stop" : "Time";
        var exit = ClosePosition(now, price, reason);
        if (exit is { } e) _ = Submit(router, e.Side, e.Qty, ct);
    }

    /// <summary>Closes the open position at <paramref name="price"/>: books cost-inclusive realized
    /// P&amp;L (spread + 2·commission + modeled slippage), records the paper trade, flattens, and
    /// returns the exit order to submit (null if already flat). Caller submits so the session-end
    /// close can be awaited while the per-tick close stays fire-and-forget.</summary>
    private (OrderSide Side, long Qty)? ClosePosition(DateTime now, double price, string reason)
    {
        if (_position == 0 || price <= 0) return null;
        var qty = Math.Abs(_position);
        var dir = _position > 0 ? 1 : -1;
        var grossRealised = _position > 0 ? (price - _entryPrice) * qty : (_entryPrice - price) * qty;

        var spread = Math.Max(_lastAsk - _lastBid, Options.SpreadCostTicks * InstrumentTick);
        var slip = ConditionalSlippage(_entryComposite);
        var costs = (spread + 2.0 * Options.CommissionPerSide + slip) * qty;
        var realised = grossRealised - costs;

        RecordSlippage(_entryComposite, slip);   // observed slippage by |C| bin for the conditional model
        _balance += realised;
        _dailyPnl += realised;
        _sessionPnl += realised;
        if (_balance > _peakEquity) _peakEquity = _balance;
        _lastTradeCloseUtc = now;

        _trades.Add(new ApexTradeRecord(_entryTimeUtc, now, dir, qty, _entryPrice, price, realised, reason));
        while (_trades.Count > MaxTrades) _trades.RemoveAt(0);

        var exitSide = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
        _position = 0;
        _pendingFillQty = qty;
        _entryTimeUtc = DateTime.MinValue;
        return (exitSide, qty);
    }

    // ── Conditional slippage model ─────────────────────────────────────────────────────────────
    private int SlipBin(double composite)
        => Math.Clamp((int)(Math.Min(Math.Abs(composite), Options.CompositeThreshold * 3.0)
            / (Options.CompositeThreshold * 3.0 + 1e-9) * SlipBins), 0, SlipBins - 1);

    private double ConditionalSlippage(double composite)
    {
        var bin = SlipBin(composite);
        if (_slipCount[bin] > 0) return _slipSum[bin] / _slipCount[bin];
        // Fall back to the Kyle linear-impact estimate (never an unconditional average of fills).
        return Options.SlippageCoefficient * Math.Abs(_lastKyleLambda) * 1.0 + _slipFallback;
    }

    private void RecordSlippage(double composite, double observed)
    {
        var bin = SlipBin(composite);
        _slipSum[bin] += observed;
        _slipCount[bin]++;
        _slipFallback = Options.SlippageCoefficient * Math.Abs(_lastKyleLambda);
    }

    // ── Regime ───────────────────────────────────────────────────────────────────────────────
    private void UpdateRegime()
    {
        var n = _barFeatures.Count;
        if (n < 12) { _regime = "Undefined"; return; }
        var feats = _barFeatures.ToArray();
        var hi = new double[n]; var lo = new double[n]; var cl = new double[n];
        for (var i = 0; i < n; i++) { hi[i] = feats[i].High; lo[i] = feats[i].Low; cl[i] = feats[i].Close; }
        var period = Math.Max(5, Math.Min(14, n / 3));
        var adx = ComputeAdx(hi, lo, cl, period);

        var meanC = Mean(cl); var sdC = Stdev(cl, meanC);
        var bbWidth = meanC > 1e-10 ? 4.0 * sdC / meanC : 0;

        int up = 0, dn = 0;
        for (var i = 0; i < n; i++) { if (cl[i] > feats[i].Open) up++; else if (cl[i] < feats[i].Open) dn++; }

        // Expanding-threshold scales with σ (dimensionless coefficient × realized BB width norm).
        var expanding = sdC / Math.Max(meanC, 1e-9);
        if (adx > 25.0 && bbWidth > expanding * 0.5)
            _regime = up >= dn ? "TrendingBull" : "TrendingBear";
        else if (adx < 18.0)
            _regime = "Ranging";
        else if (bbWidth > expanding * 2.0)
            _regime = "HighVolatility";
        else
            _regime = "Undefined";
    }

    // ── Risk ─────────────────────────────────────────────────────────────────────────────────
    private void UpdateRiskState(DateTime now, double mid)
    {
        var today = now.Date;
        if (_lastDay != today) { _lastDay = today; _dailyPnl = 0; }

        var floating = _position == 0 ? 0.0
            : _position > 0 ? (mid - _entryPrice) * _position : (_entryPrice - mid) * Math.Abs(_position);
        var equity = _balance + floating;
        if (equity > _peakEquity) _peakEquity = equity;

        if (_balance > 0 && Options.MaxDailyLossFraction > 0)
        {
            var loss = -(_dailyPnl + floating) / _balance;
            if (loss >= Options.MaxDailyLossFraction) _killSwitch = true;
        }
        if (_peakEquity > 0 && Options.MaxDrawdownFraction > 0)
        {
            var dd = (_peakEquity - equity) / _peakEquity;
            if (dd >= Options.MaxDrawdownFraction) _killSwitch = true;
        }
    }

    private bool IsSessionAllowed(DateTime nowUtc)
    {
        var h = nowUtc.Hour;
        var inAsian = h >= 0 && h < 9;
        var inLondon = h >= 8 && h < 17;
        var inNy = h >= 13 && h < 22;
        var inOverlap = h >= 13 && h < 17;
        if (inOverlap && Options.TradeLondonNy) return true;
        if (inLondon && !inOverlap && Options.TradeLondon) return true;
        if (inNy && !inOverlap && Options.TradeNewYork) return true;
        if (inAsian && Options.TradeAsian) return true;
        return false;
    }

    /// <summary>Session-dependent Kelly cap: 0.25 in the liquid London/NY overlap, 0.10 in the thin
    /// (gappier) Asian session, the configured default otherwise.</summary>
    private double SessionKellyCap(DateTime nowUtc)
    {
        var h = nowUtc.Hour;
        if (h >= 13 && h < 17) return Options.KellyFractionOverlap;   // London/NY overlap
        if (h >= 0 && h < 9) return Options.KellyFractionAsian;       // Asian
        return Options.KellyFraction;                                 // London-only / NY-only
    }

    /// <summary>Immediate book pressure on the side we trade INTO, ∈ [0, 1]: ask share for a buy
    /// (we lift offers), bid share for a sell. 0 when no fresh depth snapshot is available.</summary>
    private double OppositeBookPressure(int direction, DateTime now)
    {
        var depth = _latestDepth;
        if (depth is null) return 0;
        if ((now - _latestDepthTime).TotalMilliseconds > TtlMsFor(SigObi)) return 0;   // stale
        var bid = (double)depth.BestBidSize;
        var ask = (double)depth.BestAskSize;
        var tot = bid + ask;
        if (tot <= 0) return 0;
        return direction > 0 ? ask / tot : bid / tot;
    }

    /// <summary>Jump/gap penalty ∈ [0, 1]: the fraction of the last 100 live bars whose high-low range
    /// is at least <paramref name="barrierDistance"/> — bars big enough to gap straight through a
    /// barrier at that distance, a discontinuity the continuous first-passage formula cannot see.</summary>
    private double GapPenalty(double barrierDistance)
    {
        if (barrierDistance <= 0) return 0;
        var feats = LiveBarFeatures();
        var n = feats.Length;
        var look = Math.Min(100, n);
        if (look < 10) return 0;
        var hits = 0;
        for (var i = n - look; i < n; i++)
        {
            var range = feats[i].High - feats[i].Low;
            if (range >= barrierDistance) hits++;
        }
        return (double)hits / look;
    }

    // ── Sigma / footprint geometry ─────────────────────────────────────────────────────────────
    private void UpdateSigmaBar()
    {
        var n = _barFeatures.Count;
        if (n < 3) return;
        var feats = _barFeatures.ToArray();
        var rets = new List<double>(n);
        for (var i = 1; i < n; i++)
            if (feats[i - 1].Close > 0 && feats[i].Close > 0)
                rets.Add(Math.Log(feats[i].Close / feats[i - 1].Close));
        if (rets.Count < 2) return;
        var m = Mean(rets); var sd = Stdev(rets, m);
        if (sd > 0) _lastSigmaBar = sd;
    }

    private double FootprintTickSize()
    {
        if (Options.TickSizeOverride is { } ov && ov > 0) return ov;
        var atr = _lastSigmaBar * (_lastMid > 0 ? _lastMid : 1.0);   // σ in price units ≈ bar ATR proxy
        return FootprintFeatures.SuggestRowSize(atr, InstrumentTick, Options.RowsPerBarTarget);
    }

    private FootprintExtractorOptions ExtractorOptions() => new(Options.ImbalanceRatio);

    private double DeltaZThreshold() => 2.0;   // dimensionless z-threshold θ

    // ── Live candle ──────────────────────────────────────────────────────────────────────────
    private void TouchLiveCandle(DateTime now, double price, FootprintPrint? print = null)
    {
        if (price <= 0) return;
        var bucket = _barStart == DateTime.MinValue
            ? new DateTime(now.Ticks - now.Ticks % CandleInterval.Ticks, DateTimeKind.Utc)
            : _barStart;
        if (_liveCandle is null || _liveCandle.OpenTime != bucket)
            _liveCandle = new LiveCandleState { OpenTime = bucket, Open = price, High = price, Low = price, Close = price };
        if (price > _liveCandle.High) _liveCandle.High = price;
        if (price < _liveCandle.Low) _liveCandle.Low = price;
        _liveCandle.Close = price;
        if (print is { } p)
        {
            if (p.Aggressor == AggressorSide.Buy) _liveCandle.BuyVolume += p.Size;
            else if (p.Aggressor == AggressorSide.Sell) _liveCandle.SellVolume += p.Size;
            else { var half = p.Size / 2; _liveCandle.BuyVolume += p.Size - half; _liveCandle.SellVolume += half; }
        }
    }

    // ── Snapshot building ──────────────────────────────────────────────────────────────────────
    private ApexSnapshotV2 BuildSnapshot(DateTime now, double mid, IReadOnlyList<SignalResult> sigs,
        double composite, int direction, int agree, double gC, double condSlip, FeedQuality quality)
    {
        var lines = FitLines();
        var states = new List<ApexSignalState>(sigs.Count);
        for (var i = 0; i < sigs.Count; i++)
        {
            var r = sigs[i];
            var stale = IsStale(r.Name, now);
            var age = _signalGeneratedAt.TryGetValue(r.Name, out var ts) ? (now - ts).TotalMilliseconds : double.MaxValue;
            states.Add(new ApexSignalState(r.Name, r.Score, r.Confidence, r.Direction, r.IsValid && !stale, age, TtlMsFor(r.Name)));
        }

        _weightMap.Clear();
        for (var i = 0; i < SignalNames.Length; i++) _weightMap[SignalNames[i]] = _weights[i];

        var kyle = _lastSignal.TryGetValue(SigKyle, out var ks) ? ks : SignalResult.Invalid(SigKyle, now);
        var (epsCum, epsZ) = LastKyleResidual();
        var rho = double.IsNaN(_prevRho) ? 0 : _prevRho;
        var (zp, va) = LastValueDeviation(lines);

        var tradeAllowed = !_killSwitch && direction != 0 && agree >= 2 && IsSessionAllowed(now);
        var pred = PredictNodes();

        return new ApexSnapshotV2(
            TimestampUtc: now,
            Mid: mid,
            Signals: states,
            Composite: composite,
            CompositeDirection: direction,
            Regime: _regime,
            FeedQuality: quality,
            BootstrapMode: _bootstrapMode,
            KyleLambda: _lastKyleLambda,
            EpsilonCum: epsCum,
            EpsilonCumZ: epsZ,
            BuyLine: lines.Buy,
            SellLine: lines.Sell,
            PocLine: lines.Poc,
            ControlCoordinate: rho,
            ControlVelocity: _lastControlVelocity,
            WedgeWidth: lines.Valid ? lines.Buy.FittedEndpoint - lines.Sell.FittedEndpoint : 0,
            WedgeWidthVelocity: lines.Valid ? lines.Buy.Slope - lines.Sell.Slope : 0,
            ValueDeviationZ: zp,
            Weights: new Dictionary<string, double>(_weightMap),
            CalibratedExpectedReturn: gC,
            ConditionalSlippage: condSlip,
            SessionPnl: _sessionPnl,
            TradeAllowed: tradeAllowed,
            KillSwitch: _killSwitch,
            Position: _position,
            PredictedBuyPoc: pred.Buy,
            PredictedSellPoc: pred.Sell,
            PredictedTotalPoc: pred.Total,
            PredictedWedgeWidth: pred.Wedge,
            PredictionConfidence: pred.Conf,
            PredictionHorizonBars: pred.Horizon);
    }

    private double _lastControlVelocity;

    private ApexSnapshotV2 BuildLiveSnapshot(DateTime now, double mid, FeedQuality quality)
    {
        // Between bar closes: reuse the last computed signal states with refreshed TTL ages.
        var sigs = new SignalResult[SignalNames.Length];
        for (var i = 0; i < SignalNames.Length; i++)
            sigs[i] = _lastSignal.TryGetValue(SignalNames[i], out var s) ? s : SignalResult.Invalid(SignalNames[i], now);
        var composite = Math.Clamp(Score(sigs, now, out var dir, out var agree), -3.0, 3.0);
        var gC = EvaluateCalibration(composite);
        var condSlip = ConditionalSlippage(composite);
        return BuildSnapshot(now, mid, sigs, composite, dir, agree, gC, condSlip, quality);
    }

    private ApexSnapshotV2 BuildWarmingSnapshot(DateTime now, double mid)
    {
        var states = new List<ApexSignalState>(SignalNames.Length);
        foreach (var name in SignalNames)
            states.Add(new ApexSignalState(name, 0, 0, 0, false, 0, TtlMsFor(name)));
        var empty = ApexLineFit.Empty;
        return new ApexSnapshotV2(
            now, mid, states, 0, 0, "Warming", FeedQuality.None, true,
            0, 0, 0, empty, empty, empty, 0, 0, 0, 0, 0,
            new Dictionary<string, double>(), 0, 0, _sessionPnl, false, _killSwitch, _position);
    }

    private (double Cum, double Z) LastKyleResidual()
    {
        var feats = LiveBarFeatures();
        var n = feats.Length;
        var win = Math.Min(_kyleWindow, n);
        if (win < 6) return (0, 0);
        var rets = new double[win]; var flow = new double[win];
        for (var i = 0; i < win; i++)
        {
            var idx = n - win + i;
            var prev = idx > 0 ? feats[idx - 1].Close : feats[idx].Close;
            rets[i] = prev > 0 && feats[idx].Close > 0 ? Math.Log(feats[idx].Close / prev) : 0;
            flow[i] = feats[idx].Delta;
        }
        var fit = KyleResidual.Fit(rets, flow);
        return (fit.CumulativeResidual, fit.CumulativeResidualZ);
    }

    private (double Zp, double Va) LastValueDeviation(LineTriple l)
    {
        if (!l.Valid || l.Poc.ResidualStdev <= 1e-12) return (0, 0);
        return ((_lastMid - l.Poc.FittedEndpoint) / l.Poc.ResidualStdev, l.Poc.ResidualStdev);
    }

    private Task Submit(IOrderRouter router, OrderSide side, long qty, CancellationToken ct) =>
        router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: $"apex-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);

    // ── Math helpers ─────────────────────────────────────────────────────────────────────────
    private static double Mean(IReadOnlyList<double> v)
    {
        if (v.Count == 0) return 0;
        double s = 0; for (var i = 0; i < v.Count; i++) s += v[i];
        return s / v.Count;
    }

    private static double Stdev(IReadOnlyList<double> v, double mean)
    {
        if (v.Count <= 1) return 0;
        double a = 0; for (var i = 0; i < v.Count; i++) { var d = v[i] - mean; a += d * d; }
        return Math.Sqrt(a / v.Count);
    }

    private static double ZScore(double value, double mean, double sd) => sd < 1e-10 ? 0 : (value - mean) / sd;
    private static double ClampScore(double s) => double.IsNaN(s) ? 0 : Math.Clamp(s, -3.0, 3.0);

    private static double ComputeAdx(double[] high, double[] low, double[] close, int period)
    {
        var n = high.Length;
        if (n < period * 2) return 20.0;
        // Arrays oldest → newest.
        var dmp = new double[n]; var dmm = new double[n]; var tr = new double[n];
        for (var i = 1; i < n; i++)
        {
            var up = high[i] - high[i - 1];
            var dn = low[i - 1] - low[i];
            dmp[i] = up > dn && up > 0 ? up : 0;
            dmm[i] = dn > up && dn > 0 ? dn : 0;
            var hl = high[i] - low[i];
            var hpc = Math.Abs(high[i] - close[i - 1]);
            var lpc = Math.Abs(low[i] - close[i - 1]);
            tr[i] = Math.Max(hl, Math.Max(hpc, lpc));
        }
        var sP = WilderSmooth(dmp, period);
        var sM = WilderSmooth(dmm, period);
        var sT = WilderSmooth(tr, period);
        var dx = new double[n];
        for (var i = period; i < n; i++)
        {
            var dip = sT[i] > 1e-10 ? 100.0 * sP[i] / sT[i] : 0;
            var dim = sT[i] > 1e-10 ? 100.0 * sM[i] / sT[i] : 0;
            dx[i] = dip + dim > 1e-10 ? 100.0 * Math.Abs(dip - dim) / (dip + dim) : 0;
        }
        var adxArr = WilderSmooth(dx, period);
        return adxArr[n - 1];
    }

    private static double[] WilderSmooth(double[] raw, int period)
    {
        var n = raw.Length;
        var s = new double[n];
        if (n < period) return s;
        double seed = 0;
        for (var i = 0; i < period; i++) seed += raw[i];
        s[period - 1] = seed;
        for (var i = period; i < n; i++) s[i] = s[i - 1] - s[i - 1] / period + raw[i];
        return s;
    }

    // ── Internal records ───────────────────────────────────────────────────────────────────────
    private sealed record SignalResult(string Name, double Score, double Confidence, int Direction, bool IsValid, DateTime GeneratedAt)
    {
        public static SignalResult Invalid(string name, DateTime now) => new(name, 0, 0, 0, false, now);
        public static SignalResult From(string name, double score, double confidence, DateTime now)
        {
            var dir = score > 0.05 ? 1 : score < -0.05 ? -1 : 0;
            return new(name, score, confidence, dir, true, now);
        }
    }

    /// <summary>Per-bar features the line fits / Kyle / CVD consume.</summary>
    /// <param name="Synthetic">
    /// True for bars seeded from historical OHLCV during warm-up (<see cref="SeedFromBars"/>). Their
    /// price-structure fields (centroids, POC, OHLC) are real-derived proxies the line-fit signals
    /// can use immediately, but they carry <b>no order flow</b> (delta = 0). The flow signals
    /// (delta / Kyle / CVD) filter these out via <see cref="LiveBarFeatures"/> so they never score
    /// off fabricated flow; they warm strictly from live tape.
    /// </param>
    private readonly record struct BarFeatures(
        DateTime EndUtc, double Open, double High, double Low, double Close,
        double BuyCentroid, double SellCentroid, double PocPrice, int PocValid,
        long Delta, long CumulativeDelta, long Volume, double PocOrClose,
        bool Synthetic = false);

    private readonly record struct LineTriple(ApexLineFit Buy, ApexLineFit Sell, ApexLineFit Poc, double EndX)
    {
        public static LineTriple Empty => new(ApexLineFit.Empty, ApexLineFit.Empty, ApexLineFit.Empty, 0);
        public bool Valid => Buy.FittedEndpoint > 0 && Sell.FittedEndpoint > 0;
    }

    private sealed class LiveCandleState
    {
        public DateTime OpenTime;
        public double Open, High, Low, Close;
        public long BuyVolume, SellVolume;
    }

    private sealed class TapeArrivalWindow
    {
        private readonly Queue<(DateTime Time, double Price)> _ring = new();
        private const int Capacity = 4_096;

        public void Add(FootprintPrint p)
        {
            _ring.Enqueue((p.TimeUtc, p.Price));
            while (_ring.Count > Capacity) _ring.Dequeue();
        }

        public double ArrivalRate(DateTime now, double windowSec)
        {
            if (windowSec <= 0 || _ring.Count == 0) return 0;
            var threshold = now - TimeSpan.FromSeconds(windowSec);
            var count = 0;
            foreach (var t in _ring) if (t.Time >= threshold) count++;
            return count / windowSec;
        }

        public double UpTickFraction(DateTime now, double windowSec)
        {
            if (_ring.Count == 0) return 0.5;
            var threshold = now - TimeSpan.FromSeconds(windowSec);
            int up = 0, total = 0; double prev = 0;
            foreach (var t in _ring)
            {
                if (t.Time < threshold) { prev = t.Price; continue; }
                if (prev > 0)
                {
                    total++;
                    if (t.Price > prev) up++;
                }
                prev = t.Price;
            }
            return total == 0 ? 0.5 : (double)up / total;
        }
    }

    private sealed class RingBuffer<T>
    {
        private readonly Queue<T> _q;
        private readonly int _capacity;
        public RingBuffer(int capacity) { _capacity = Math.Max(1, capacity); _q = new Queue<T>(_capacity); }
        public int Count => _q.Count;
        public int Capacity => _capacity;
        public void Push(T v) { _q.Enqueue(v); while (_q.Count > _capacity) _q.Dequeue(); }
        public T[] ToArray() => _q.ToArray();
    }
}
