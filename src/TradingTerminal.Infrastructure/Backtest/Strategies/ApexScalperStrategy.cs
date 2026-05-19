using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>Per-signal score line for UI exposure. Mirrors the internal SignalResult but
/// promoted to public so view-models can bind to it without leaking the rest of the class.</summary>
public sealed record ApexSignalScore(double Score, double Confidence, int Direction, bool IsValid);

/// <summary>Flat snapshot of the strategy state at a point in time. Built after each tick
/// for the live dashboard; pushed into a history ring on each completed internal candle so
/// per-indicator charts can draw a recent time series.</summary>
public sealed record ApexSnapshot(
    DateTime TimestampUtc,
    double Mid,
    ApexSignalScore Delta,
    ApexSignalScore Vpin,
    ApexSignalScore ObiShallow,
    ApexSignalScore ObiDeep,
    ApexSignalScore Footprint,
    ApexSignalScore Absorption,
    ApexSignalScore Hvp,
    ApexSignalScore TapeSpeed,
    double Composite,
    int CompositeDirection,
    int SignalsAgree,
    int SignalsConflict,
    bool ConflictFlag,
    bool TradeAllowed,
    string Regime,
    bool KillSwitch,
    double DailyPnl,
    double Balance,
    double PeakEquity,
    long Position);

/// <summary>
/// APEX microstructure scalper — port of the MT5 ApexScalper EA at
/// <c>D:\Github\Trading Strategies\platforms\MT5\ApexScalper</c>. Combines 8 order-flow
/// signals into a weighted composite, applies a regime-adaptive multiplier, runs a
/// conflict filter, and trades through a confirmation gate that layers session, spread,
/// daily-loss and cooldown checks on top. Stops are anchored behind the nearest
/// high-volume pocket; targets sit at the opposite HVP with fixed-RR fallback.
///
/// Data plumbing follows the codebase convention (see <see cref="OrderFlowToxicityStrategy"/>
/// and <see cref="BookPressureStrategy"/>): backtest ticks are L1 quote-only, so
/// trade-side classification uses the tick rule (mid up ⇒ buy-initiated) and "volume"
/// is proxied by <c>BidSize + AskSize</c>. When the parquet reader learns to carry a
/// <see cref="DepthSnapshot"/> the OBI signals can be swapped to true multi-level
/// imbalance without changing the public surface.
/// </summary>
public sealed class ApexScalperStrategy : IBacktestStrategy
{
    // ── Parameters (1:1 with MT5 Inputs.mqh) ────────────────────────────────────────
    public int WindowSize { get; }
    public TimeSpan CandleInterval { get; }

    public double WeightDelta { get; }
    public double WeightVpin { get; }
    public double WeightObiShallow { get; }
    public double WeightObiDeep { get; }
    public double WeightFootprint { get; }
    public double WeightHvp { get; }
    public double WeightAbsorption { get; }
    public double WeightTapeSpeed { get; }

    public double CompositeThreshold { get; }
    public int MinSignalsAgree { get; }
    public bool EnableConflictFilter { get; }
    public double ConflictScoreBand { get; }

    public double DeltaZScoreThreshold { get; }
    public int DeltaAccelPeriod { get; }
    public double DeltaEfficiencyMin { get; }
    public bool EnableDeltaDivergence { get; }

    public long VpinBucketVolume { get; }
    public int VpinLookbackBuckets { get; }
    public double VpinThreshold { get; }

    public int ObiLevelsDeep { get; }
    public double ObiWeightDecay { get; }
    public int ObiMomentumPeriod { get; }

    public int MinStackedRows { get; }
    public double ImbalanceRatio { get; }

    public double AbsorptionMinVolume { get; }
    public int AbsorptionLookback { get; }

    public double HvpStdDevMultiplier { get; }
    public int HvpMinNodes { get; }
    public double HvpSlopeThreshold { get; }

    public int TapeSpeedWindowSec { get; }
    public double TapeSpeedZScore { get; }
    public double TapeSpeedDirectional { get; }

    public int TtlMsDelta { get; }
    public int TtlMsVpin { get; }
    public int TtlMsObi { get; }
    public int TtlMsFootprint { get; }
    public int TtlMsAbsorption { get; }
    public int TtlMsHvp { get; }
    public int TtlMsTapeSpeed { get; }

    public int AdxPeriod { get; }
    public double AdxTrendingThreshold { get; }
    public double AdxRangingThreshold { get; }
    public double BbWidthExpanding { get; }

    public double RiskPercent { get; }
    public bool UseDynamicSizing { get; }
    public long FixedQuantity { get; }
    public double StartingBalance { get; }

    public bool SlBehindHvp { get; }
    public double SlFixedPriceUnits { get; }
    public double SlBufferPriceUnits { get; }
    public bool TpAtOppositeHvp { get; }
    public double TpFixedRr { get; }

    public double MaxDailyLossPercent { get; }
    public double MaxDrawdownPercent { get; }
    public int MinSecondsBetweenTrades { get; }

    public bool TradeAsian { get; }
    public bool TradeLondon { get; }
    public bool TradeNewYork { get; }
    public bool TradeLondonNy { get; }
    public double MaxSpreadPriceUnits { get; }

    // ── Wiring ──────────────────────────────────────────────────────────────────────
    private readonly Contract _contract;
    private int _orderSeq;

    // ── Data layer ──────────────────────────────────────────────────────────────────
    private readonly TickCollector _tc;
    private readonly CandleBuilder _cb;
    private readonly FootprintBuilder _fb;
    private readonly VolumeProfile _vp;

    // ── Signals ─────────────────────────────────────────────────────────────────────
    private readonly VpinAccumulator _vpinAccum;
    private readonly RingBuffer<double> _tapeSpeedHistory = new(60);

    // ── Engine state ────────────────────────────────────────────────────────────────
    private readonly Dictionary<string, DateTime> _signalGeneratedAt = new();
    private RegimeKind _regime = RegimeKind.Undefined;

    // ── Risk state ──────────────────────────────────────────────────────────────────
    private double _balance;
    private double _peakEquity;
    private double _dailyPnl;
    private DateTime _lastDay = DateTime.MinValue;
    private DateTime _lastTradeCloseUtc = DateTime.MinValue;
    private bool _killSwitch;

    // ── Position state ──────────────────────────────────────────────────────────────
    private long _position;             // signed: + long, - short, 0 flat
    private long _pendingFillQty;       // qty currently working but not yet filled
    private double _entryPrice;
    private double _stopPrice;
    private double _targetPrice;

    // ── Snapshot history (UI binding) ──────────────────────────────────────────────
    private const int MaxHistory = 500;
    private readonly LinkedList<ApexSnapshot> _history = new();

    /// <summary>Most recent snapshot built after composite scoring. <c>null</c> until the
    /// window has warmed up (≥ <see cref="WindowSize"/> completed candles).</summary>
    public ApexSnapshot? Latest { get; private set; }

    /// <summary>Snapshots taken at each completed internal candle, oldest first. Capped at
    /// <see cref="MaxHistory"/>; callers should trim to a tail length they care about.</summary>
    public IReadOnlyList<ApexSnapshot> History
    {
        get
        {
            var list = new List<ApexSnapshot>(_history.Count);
            foreach (var s in _history) list.Add(s);
            return list;
        }
    }

    public ApexScalperStrategy(
        Contract contract,
        int windowSize = 20,
        TimeSpan? candleInterval = null,
        // Weights
        double weightDelta = 0.20, double weightVpin = 0.20,
        double weightObiShallow = 0.15, double weightObiDeep = 0.10,
        double weightFootprint = 0.15, double weightHvp = 0.05,
        double weightAbsorption = 0.10, double weightTapeSpeed = 0.05,
        // Scoring
        double compositeThreshold = 1.80, int minSignalsAgree = 4,
        bool enableConflictFilter = true, double conflictScoreBand = 0.30,
        // Delta
        double deltaZScoreThreshold = 2.0, int deltaAccelPeriod = 3,
        double deltaEfficiencyMin = 0.15, bool enableDeltaDivergence = true,
        // VPIN
        long vpinBucketVolume = 5_000, int vpinLookbackBuckets = 10,
        double vpinThreshold = 0.70,
        // OBI
        int obiLevelsDeep = 10, double obiWeightDecay = 0.5,
        int obiMomentumPeriod = 5,
        // Footprint
        int minStackedRows = 3, double imbalanceRatio = 3.0,
        // Absorption / HVP
        double absorptionMinVolume = 10_000, int absorptionLookback = 5,
        double hvpStdDevMultiplier = 1.5, int hvpMinNodes = 3,
        double hvpSlopeThreshold = 0.5,
        // Tape speed
        int tapeSpeedWindowSec = 10, double tapeSpeedZScore = 2.0,
        double tapeSpeedDirectional = 0.65,
        // TTLs (ms)
        int ttlMsDelta = 5_000, int ttlMsVpin = 8_000, int ttlMsObi = 2_000,
        int ttlMsFootprint = 10_000, int ttlMsAbsorption = 15_000,
        int ttlMsHvp = 20_000, int ttlMsTapeSpeed = 3_000,
        // Regime
        int adxPeriod = 14, double adxTrendingThreshold = 25.0,
        double adxRangingThreshold = 18.0, double bbWidthExpanding = 0.002,
        // Sizing
        double riskPercent = 0.5, bool useDynamicSizing = true,
        long fixedQuantity = 1, double startingBalance = 100_000,
        // SL / TP
        bool slBehindHvp = true, double slFixedPriceUnits = 0.0015,
        double slBufferPriceUnits = 0.0002,
        bool tpAtOppositeHvp = true, double tpFixedRr = 1.5,
        // Risk / sessions
        double maxDailyLossPercent = 2.0, double maxDrawdownPercent = 5.0,
        int minSecondsBetweenTrades = 30,
        bool tradeAsian = false, bool tradeLondon = true,
        bool tradeNewYork = true, bool tradeLondonNy = true,
        double maxSpreadPriceUnits = 0.0030)
    {
        _contract = contract;
        WindowSize = windowSize;
        CandleInterval = candleInterval ?? TimeSpan.FromMinutes(1);

        // Auto-normalise weights so the sum is 1 (matches MT5 ScoringEngine.Initialize)
        var rawSum = weightDelta + weightVpin + weightObiShallow + weightObiDeep
                   + weightFootprint + weightHvp + weightAbsorption + weightTapeSpeed;
        if (rawSum <= 1e-10) rawSum = 1.0;
        WeightDelta = weightDelta / rawSum;
        WeightVpin = weightVpin / rawSum;
        WeightObiShallow = weightObiShallow / rawSum;
        WeightObiDeep = weightObiDeep / rawSum;
        WeightFootprint = weightFootprint / rawSum;
        WeightHvp = weightHvp / rawSum;
        WeightAbsorption = weightAbsorption / rawSum;
        WeightTapeSpeed = weightTapeSpeed / rawSum;

        CompositeThreshold = compositeThreshold;
        MinSignalsAgree = minSignalsAgree;
        EnableConflictFilter = enableConflictFilter;
        ConflictScoreBand = conflictScoreBand;

        DeltaZScoreThreshold = deltaZScoreThreshold;
        DeltaAccelPeriod = deltaAccelPeriod;
        DeltaEfficiencyMin = deltaEfficiencyMin;
        EnableDeltaDivergence = enableDeltaDivergence;

        VpinBucketVolume = vpinBucketVolume;
        VpinLookbackBuckets = vpinLookbackBuckets;
        VpinThreshold = vpinThreshold;

        ObiLevelsDeep = obiLevelsDeep;
        ObiWeightDecay = obiWeightDecay;
        ObiMomentumPeriod = obiMomentumPeriod;

        MinStackedRows = minStackedRows;
        ImbalanceRatio = imbalanceRatio;

        AbsorptionMinVolume = absorptionMinVolume;
        AbsorptionLookback = absorptionLookback;

        HvpStdDevMultiplier = hvpStdDevMultiplier;
        HvpMinNodes = hvpMinNodes;
        HvpSlopeThreshold = hvpSlopeThreshold;

        TapeSpeedWindowSec = tapeSpeedWindowSec;
        TapeSpeedZScore = tapeSpeedZScore;
        TapeSpeedDirectional = tapeSpeedDirectional;

        TtlMsDelta = ttlMsDelta;
        TtlMsVpin = ttlMsVpin;
        TtlMsObi = ttlMsObi;
        TtlMsFootprint = ttlMsFootprint;
        TtlMsAbsorption = ttlMsAbsorption;
        TtlMsHvp = ttlMsHvp;
        TtlMsTapeSpeed = ttlMsTapeSpeed;

        AdxPeriod = adxPeriod;
        AdxTrendingThreshold = adxTrendingThreshold;
        AdxRangingThreshold = adxRangingThreshold;
        BbWidthExpanding = bbWidthExpanding;

        RiskPercent = riskPercent;
        UseDynamicSizing = useDynamicSizing;
        FixedQuantity = fixedQuantity;
        StartingBalance = startingBalance;

        SlBehindHvp = slBehindHvp;
        SlFixedPriceUnits = slFixedPriceUnits;
        SlBufferPriceUnits = slBufferPriceUnits;
        TpAtOppositeHvp = tpAtOppositeHvp;
        TpFixedRr = tpFixedRr;

        MaxDailyLossPercent = maxDailyLossPercent;
        MaxDrawdownPercent = maxDrawdownPercent;
        MinSecondsBetweenTrades = minSecondsBetweenTrades;
        TradeAsian = tradeAsian;
        TradeLondon = tradeLondon;
        TradeNewYork = tradeNewYork;
        TradeLondonNy = tradeLondonNy;
        MaxSpreadPriceUnits = maxSpreadPriceUnits;

        _tc = new TickCollector(capacity: 5_000);
        _cb = new CandleBuilder(WindowSize + 5, CandleInterval);
        _fb = new FootprintBuilder(WindowSize + 5);
        _vp = new VolumeProfile(maxNodes: 2_000);

        _vpinAccum = new VpinAccumulator(maxBuckets: 50, bucketVolume: VpinBucketVolume);

        _balance = StartingBalance;
        _peakEquity = StartingBalance;
    }

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public async Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        // 1. Feed the tick into the data layer (classify, build candle / footprint, accumulate VPIN).
        var classified = _tc.Push(tick);
        var (rolled, lastCandle) = _cb.Push(classified);
        _fb.Push(classified, _cb.CurrentBarStart);
        _vpinAccum.Push(classified);
        _tapeSpeedHistory.Push(_tc.TapeSpeed(TapeSpeedWindowSec));

        // When a candle rolls, push it into the volume profile and refresh footprint completion.
        if (rolled && lastCandle is not null)
        {
            _vp.AddCandle(lastCandle, _fb.LastCompleted);
            _fb.CompleteLast();
        }

        // 2. Risk / regime maintenance (every tick is cheap; regime only refreshes on bar roll).
        UpdateRiskState(tick, classified.Mid);
        if (rolled) UpdateRegime();
        MaintainPosition(tick, router, ct);

        // 3. Compute signals. We need a warm window before scoring is meaningful.
        if (_cb.CompletedCount < WindowSize) return;

        var now = tick.TimestampUtc;
        var results = new SignalResult[8];
        results[0] = CalcDelta(now);
        results[1] = CalcVpin(now);
        results[2] = CalcObiShallow(tick, now);
        results[3] = CalcObiDeep(tick, now);
        results[4] = CalcFootprint(classified.Mid, now);
        results[5] = CalcAbsorption(now);
        results[6] = CalcHvp(now);
        results[7] = CalcTapeSpeed(now);

        // 4. Composite scoring with regime multipliers + TTL decay + conflict filter.
        var composite = Score(results, now);

        // 4b. Snapshot the state — live dashboard reads Latest; charts read History.
        var snap = BuildSnapshot(now, classified.Mid, results, composite);
        Latest = snap;
        if (rolled)
        {
            _history.AddLast(snap);
            while (_history.Count > MaxHistory) _history.RemoveFirst();
        }

        // 5. Confirmation gate. All filters in one place — if anything blocks, return.
        if (!ShouldTrade(composite, tick, classified.Mid, now, out var direction)) return;
        if (_position != 0 || _pendingFillQty != 0) return;

        // 6. Place stops, size, fire.
        var entry = direction > 0 ? tick.Ask : tick.Bid;
        if (entry <= 0) return;
        var sl = ComputeStop(direction, entry);
        if (sl <= 0) return;
        var slDist = Math.Abs(entry - sl);
        if (slDist <= 0) return;
        var tp = ComputeTarget(direction, entry, slDist);

        var qty = UseDynamicSizing
            ? DynamicQuantity(slDist)
            : FixedQuantity;
        if (qty <= 0) return;

        _entryPrice = entry;
        _stopPrice = sl;
        _targetPrice = tp;
        _pendingFillQty = qty;
        _position = direction > 0 ? qty : -qty;

        await Submit(router, direction > 0 ? OrderSide.Buy : OrderSide.Sell, qty, ct);
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct)
    {
        if (evt.State == OrderState.Filled)
        {
            _pendingFillQty = 0;
            // When a flatten order fills, _position is already zeroed in MaintainPosition.
        }
        return Task.CompletedTask;
    }

    public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (_position == 0) return;
        var side = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
        await Submit(router, side, Math.Abs(_position), ct);
        _position = 0;
    }

    // ── Position management ────────────────────────────────────────────────────────
    private void MaintainPosition(Tick tick, IOrderRouter router, CancellationToken ct)
    {
        if (_position == 0) return;

        var price = _position > 0 ? tick.Bid : tick.Ask;
        if (price <= 0) return;

        var hitStop = (_position > 0 && price <= _stopPrice)
                   || (_position < 0 && price >= _stopPrice);
        var hitTarget = (_position > 0 && price >= _targetPrice)
                     || (_position < 0 && price <= _targetPrice);

        if (!hitStop && !hitTarget) return;

        var exitSide = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
        var qty = Math.Abs(_position);
        var realised = _position > 0 ? (price - _entryPrice) * qty : (_entryPrice - price) * qty;

        _balance += realised;
        _dailyPnl += realised;
        if (_balance > _peakEquity) _peakEquity = _balance;
        _lastTradeCloseUtc = tick.TimestampUtc;
        _position = 0;
        _pendingFillQty = qty;

        _ = Submit(router, exitSide, qty, ct);
    }

    // ── Confirmation gate ──────────────────────────────────────────────────────────
    private bool ShouldTrade(CompositeResult cr, Tick tick, double mid, DateTime now, out int direction)
    {
        direction = 0;
        if (_killSwitch) return false;
        if (!cr.TradeAllowed) return false;
        if (Math.Abs(cr.Score) < CompositeThreshold) return false;
        if (cr.SignalsAgree < MinSignalsAgree) return false;
        if (_regime == RegimeKind.HighVolatility) return false;

        if (!IsSessionAllowed(now)) return false;

        var spread = tick.Ask - tick.Bid;
        if (MaxSpreadPriceUnits > 0 && spread > MaxSpreadPriceUnits) return false;

        var secondsSinceClose = (now - _lastTradeCloseUtc).TotalSeconds;
        if (_lastTradeCloseUtc != DateTime.MinValue && secondsSinceClose < MinSecondsBetweenTrades) return false;

        direction = cr.Direction;
        return direction != 0;
    }

    // ── Scoring ────────────────────────────────────────────────────────────────────
    private CompositeResult Score(IReadOnlyList<SignalResult> results, DateTime now)
    {
        double weightedSum = 0, activeWeight = 0;
        var agree = 0; var conflict = 0;

        // Pass 1: refresh TTL stamps and accumulate weighted contribution.
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (r.IsValid) _signalGeneratedAt[r.Name] = r.GeneratedAt;

            // TTL override — stale signals don't participate.
            if (r.IsValid && IsStale(r.Name, now)) r = r with { IsValid = false };
            if (!r.IsValid) continue;

            var baseW = BaseWeight(r.Name);
            var adjW = baseW * RegimeMultiplier(r.Name, _regime);
            weightedSum += r.Score * adjW * r.Confidence;
            activeWeight += adjW;
        }

        var score = activeWeight > 1e-10 ? weightedSum / activeWeight : 0.0;
        var dir = score > 0.05 ? 1 : score < -0.05 ? -1 : 0;

        // Pass 2: count direction agreement among "strong" valid signals.
        SignalResult? delta = null, vpin = null;
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (r.IsValid && IsStale(r.Name, now)) r = r with { IsValid = false };
            if (!r.IsValid || Math.Abs(r.Score) < 0.5) continue;
            if (r.Direction == dir) agree++;
            else if (r.Direction != 0) conflict++;
            if (r.Name == "DELTA") delta = r;
            else if (r.Name == "VPIN") vpin = r;
        }

        var conflictFlag = false;
        if (EnableConflictFilter
            && delta is { IsValid: true } d
            && vpin is { IsValid: true } v
            && Math.Abs(d.Score) > 0.5 && Math.Abs(v.Score) > 0.5
            && d.Direction != v.Direction && d.Direction != 0 && v.Direction != 0
            && Math.Abs(score) < CompositeThreshold + ConflictScoreBand)
        {
            conflictFlag = true;
        }

        var agreementOk = agree >= MinSignalsAgree;
        return new CompositeResult(
            Score: score,
            Direction: dir,
            SignalsAgree: agree,
            SignalsConflict: conflict,
            ConflictFlag: conflictFlag,
            TradeAllowed: agreementOk && !conflictFlag,
            Confidence: Math.Clamp(activeWeight, 0.0, 1.0));
    }

    private double BaseWeight(string name) => name switch
    {
        "DELTA" => WeightDelta,
        "VPIN" => WeightVpin,
        "OBI_SHALLOW" => WeightObiShallow,
        "OBI_DEEP" => WeightObiDeep,
        "FOOTPRINT" => WeightFootprint,
        "HVP" => WeightHvp,
        "ABSORPTION" => WeightAbsorption,
        "TAPE_SPEED" => WeightTapeSpeed,
        _ => 0.0,
    };

    private static double RegimeMultiplier(string name, RegimeKind regime) => regime switch
    {
        RegimeKind.TrendingBull or RegimeKind.TrendingBear => name switch
        {
            "DELTA" => 1.3, "HVP" => 1.2, "OBI_DEEP" => 0.8, _ => 1.0,
        },
        RegimeKind.Ranging => name switch
        {
            "OBI_SHALLOW" => 1.3, "ABSORPTION" => 1.3, "DELTA" => 0.7, _ => 1.0,
        },
        RegimeKind.HighVolatility => 0.5,
        _ => 1.0,
    };

    private bool IsStale(string name, DateTime now)
    {
        if (!_signalGeneratedAt.TryGetValue(name, out var ts)) return true;
        var ttlMs = name switch
        {
            "DELTA" => TtlMsDelta,
            "VPIN" => TtlMsVpin,
            "OBI_SHALLOW" or "OBI_DEEP" => TtlMsObi,
            "FOOTPRINT" => TtlMsFootprint,
            "ABSORPTION" => TtlMsAbsorption,
            "HVP" => TtlMsHvp,
            "TAPE_SPEED" => TtlMsTapeSpeed,
            _ => 5_000,
        };
        return (now - ts).TotalMilliseconds >= ttlMs;
    }

    // ── Signal: cumulative delta ───────────────────────────────────────────────────
    private SignalResult CalcDelta(DateTime now)
    {
        var window = _cb.Window;
        var n = window.Count;
        if (n < WindowSize) return SignalResult.Invalid("DELTA", now);
        if (n <= DeltaAccelPeriod) return SignalResult.Invalid("DELTA", now);

        // Sub-A: Z-score of newest tick_delta vs the window.
        var deltas = new double[n];
        for (var i = 0; i < n; i++) deltas[i] = window[i].TickDelta;
        var meanD = Mean(deltas, n);
        var sdD = Stdev(deltas, n, meanD);
        var z = ZScore(deltas[0], meanD, sdD);
        var scoreA = ScoreFromZ(z, DeltaZScoreThreshold);
        var confA = Math.Clamp(Math.Abs(z) / (DeltaZScoreThreshold * 2.0), 0, 1);

        // Sub-B: acceleration of delta.
        var accelN = n - DeltaAccelPeriod;
        var accels = new double[accelN];
        for (var i = 0; i < accelN; i++) accels[i] = window[i].TickDelta - window[i + DeltaAccelPeriod].TickDelta;
        var meanA = Mean(accels, accelN);
        var sdA = Stdev(accels, accelN, meanA);
        var zA = ZScore(accels[0], meanA, sdA);
        var scoreB = ScoreFromZ(zA, DeltaZScoreThreshold);
        var confB = Math.Clamp(Math.Abs(zA) / (DeltaZScoreThreshold * 2.0), 0, 1);

        // Sub-C: divergence — price at window extreme while recent delta disagrees.
        var scoreC = 0.0;
        var confC = (confA + confB) * 0.5;
        if (EnableDeltaDivergence && n >= 4)
        {
            double winHigh = window[0].High, winLow = window[0].Low;
            for (var i = 1; i < n; i++)
            {
                if (window[i].High > winHigh) winHigh = window[i].High;
                if (window[i].Low < winLow) winLow = window[i].Low;
            }
            var recentDelta = window[0].TickDelta + window[1].TickDelta + window[2].TickDelta;
            var tol = 2.0 * Math.Max(window[0].Close * 1e-5, 1e-6);
            if (Math.Abs(window[0].High - winHigh) < tol && recentDelta < 0) scoreC = -2.0;
            else if (Math.Abs(window[0].Low - winLow) < tol && recentDelta > 0) scoreC = +2.0;
            if (window[0].DeltaEfficiency < DeltaEfficiencyMin) confC = Math.Max(0, confC - 0.4);
        }

        var score = scoreA * 0.4 + scoreB * 0.3 + scoreC * 0.3;
        var conf = confA * 0.4 + confB * 0.3 + confC * 0.3;
        return SignalResult.From("DELTA", score, conf, now);
    }

    // ── Signal: VPIN ───────────────────────────────────────────────────────────────
    private SignalResult CalcVpin(DateTime now)
    {
        if (_vpinAccum.Count < 2) return SignalResult.Invalid("VPIN", now);
        var look = Math.Min(VpinLookbackBuckets, _vpinAccum.Count);
        var vpin = _vpinAccum.AverageToxicity(look);
        var rising = _vpinAccum.IsRising();
        var latest = _vpinAccum.Latest;
        var buyHeavy = latest.BuyFraction > 0.5;
        var conf = Math.Clamp(vpin, 0, 1);

        double score;
        if (vpin >= VpinThreshold)
        {
            var excess = (vpin - VpinThreshold) / Math.Max(1.0 - VpinThreshold, 1e-10);
            var baseScore = 2.5 + excess * 0.5;
            score = buyHeavy ? baseScore : -baseScore;
            if (rising) score += score > 0 ? 0.3 : -0.3;
        }
        else
        {
            var prop = Math.Clamp(vpin / Math.Max(VpinThreshold, 1e-10) * 1.5, 0, 1.5);
            score = buyHeavy ? prop : -prop;
            if (rising) score += score > 0 ? 0.3 : -0.3;
        }
        score = Math.Clamp(score, -3.0, 3.0);
        return SignalResult.From("VPIN", score, conf, now);
    }

    // ── Signal: OBI (L1 fallback — see class comment) ─────────────────────────────
    private SignalResult CalcObiShallow(Tick tick, DateTime now)
    {
        var total = tick.BidSize + tick.AskSize;
        if (total <= 0) return SignalResult.Invalid("OBI_SHALLOW", now);
        var obi = (double)(tick.BidSize - tick.AskSize) / total;
        var score = Math.Clamp(obi * 3.0, -3.0, 3.0);
        var conf = Math.Abs(obi);
        return SignalResult.From("OBI_SHALLOW", score, conf, now);
    }

    private SignalResult CalcObiDeep(Tick tick, DateTime now)
    {
        // Without DepthSnapshot we degenerate to the same L1 imbalance; the slight twist
        // is that the deep-OBI weight is smaller, so it still differentiates from shallow.
        var total = tick.BidSize + tick.AskSize;
        if (total <= 0) return SignalResult.Invalid("OBI_DEEP", now);
        var obi = (double)(tick.BidSize - tick.AskSize) / total;
        var score = Math.Clamp(obi * 3.0, -3.0, 3.0);
        var conf = Math.Abs(obi);
        return SignalResult.From("OBI_DEEP", score, conf, now);
    }

    // ── Signal: footprint stacked imbalance ────────────────────────────────────────
    private SignalResult CalcFootprint(double mid, DateTime now)
    {
        var fps = _fb.Completed;
        var check = Math.Min(fps.Count, 3);
        if (check == 0) return SignalResult.Invalid("FOOTPRINT", now);

        double sum = 0;
        for (var i = 0; i < check; i++) sum += StackedScore(fps[i]);
        var score = Math.Clamp(sum / check, -3.0, 3.0);

        var live = _fb.Live;
        if (live.StackedBull >= MinStackedRows - 1 && score >= 0) score = Math.Clamp(score + 0.5, -3.0, 3.0);
        if (live.StackedBear >= MinStackedRows - 1 && score <= 0) score = Math.Clamp(score - 0.5, -3.0, 3.0);

        // Fill-target nudge: are we approaching a known imbalance zone?
        score += FillTargetBonus(fps, check, mid);
        score = Math.Clamp(score, -3.0, 3.0);

        var conf = Math.Abs(score) / 3.0;
        return SignalResult.From("FOOTPRINT", score, conf, now);
    }

    private double StackedScore(FootprintCandle fc)
    {
        var min = MinStackedRows;
        var bull = fc.StackedBull >= min ? Math.Clamp(fc.StackedBull - min + 1, 0, 3) : 0.0;
        var bear = fc.StackedBear >= min ? Math.Clamp(fc.StackedBear - min + 1, 0, 3) : 0.0;
        return bull - bear;
    }

    private double FillTargetBonus(IReadOnlyList<FootprintCandle> fps, int count, double price)
    {
        var nudge = 20 * Math.Max(price * 1e-5, 1e-6);
        double score = 0;
        for (var c = 0; c < count && c < 3; c++)
        {
            foreach (var row in fps[c].Rows)
            {
                if (row.AskImbalance || row.ZeroBid)
                {
                    var dist = price - row.Price;
                    if (dist > 0 && dist < nudge) { score += 0.5; break; }
                }
                if (row.BidImbalance || row.ZeroAsk)
                {
                    var dist = row.Price - price;
                    if (dist > 0 && dist < nudge) { score -= 0.5; break; }
                }
            }
        }
        return Math.Clamp(score, -1.0, 1.0);
    }

    // ── Signal: absorption ─────────────────────────────────────────────────────────
    private SignalResult CalcAbsorption(DateTime now)
    {
        var window = _cb.Window;
        var n = window.Count;
        var lookback = Math.Min(AbsorptionLookback, n);
        if (lookback == 0) return SignalResult.Invalid("ABSORPTION", now);

        var qualifying = new List<double>(n);
        for (var i = 0; i < n; i++)
            if (window[i].Volume >= AbsorptionMinVolume) qualifying.Add(Absorption(window[i]));
        if (qualifying.Count < 2) return SignalResult.Invalid("ABSORPTION", now);

        var arr = qualifying.ToArray();
        var mean = Mean(arr, arr.Length);
        var sd = Stdev(arr, arr.Length, mean);

        double bestScore = 0, bestConf = 0;
        var keyBuf = SlBufferPriceUnits * 2.0;
        for (var i = 0; i < lookback; i++)
        {
            var c = window[i];
            if (c.Volume < AbsorptionMinVolume) continue;
            var zA = ZScore(Absorption(c), mean, sd);
            if (zA < 1.0) continue;

            var mid = (c.High + c.Low) * 0.5;
            var atKey = _vp.NearHvp(mid, keyBuf);
            var maxScore = atKey ? 3.0 : 1.0;
            var scaled = Math.Clamp(zA / DeltaZScoreThreshold * maxScore, 0, maxScore);
            var conf = Math.Clamp(zA / (DeltaZScoreThreshold * 2.0), 0, 1);

            var range = Math.Max(c.High - c.Low, 1e-9);
            var closePct = (c.Close - c.Low) / range;
            var directional = closePct < 0.4 ? -scaled : scaled;
            if (Math.Abs(directional) > Math.Abs(bestScore))
            {
                bestScore = directional; bestConf = conf;
            }
        }
        if (bestScore == 0) return SignalResult.From("ABSORPTION", 0, 0, now);
        return SignalResult.From("ABSORPTION", bestScore, bestConf, now);
    }

    private static double Absorption(EnrichedCandle c)
    {
        var range = Math.Max(c.High - c.Low, 1e-9);
        return c.Volume / range;
    }

    // ── Signal: HVP regression slope ───────────────────────────────────────────────
    private SignalResult CalcHvp(DateTime now)
    {
        if (!_vp.IsReady) return SignalResult.Invalid("HVP", now);
        var hvps = _vp.Hvps(HvpStdDevMultiplier);
        if (hvps.Count < HvpMinNodes) return SignalResult.Invalid("HVP", now);

        // X = bar index (oldest=0), Y = price, W = volume / (bars_since_peak + 1).
        var n = hvps.Count;
        var xs = new double[n]; var ys = new double[n]; var ws = new double[n];
        for (var i = 0; i < n; i++)
        {
            var node = hvps[i];
            xs[i] = node.BarFromOldest;
            ys[i] = node.Price;
            ws[i] = node.Volume / (node.BarFromNewest + 1.0);
        }
        var slope = WeightedSlope(xs, ys, ws, n);
        var r2 = Math.Clamp(Rsquared(xs, ys, n), 0, 1);

        double score = 0;
        if (Math.Abs(slope) >= HvpSlopeThreshold)
            score = Math.Clamp(slope / (HvpSlopeThreshold * 2.0) * 3.0, -3.0, 3.0);
        score *= r2;
        return SignalResult.From("HVP", score, r2, now);
    }

    // ── Signal: tape speed ─────────────────────────────────────────────────────────
    private SignalResult CalcTapeSpeed(DateTime now)
    {
        if (_tapeSpeedHistory.Count < 30) return SignalResult.Invalid("TAPE_SPEED", now);
        var arr = _tapeSpeedHistory.ToArray();
        var mean = Mean(arr, arr.Length);
        var sd = Stdev(arr, arr.Length, mean);
        var curr = _tc.TapeSpeed(TapeSpeedWindowSec);
        var dirFrac = _tc.DirectionalFraction(TapeSpeedWindowSec);
        var z = ZScore(curr, mean, sd);

        double score = 0;
        var conf = Math.Clamp(Math.Abs(z) / (TapeSpeedZScore * 2.0), 0, 1);
        if (z > TapeSpeedZScore)
        {
            if (dirFrac > TapeSpeedDirectional) score = ScoreFromZ(z, TapeSpeedZScore);
            else if (dirFrac < 1.0 - TapeSpeedDirectional) score = -ScoreFromZ(z, TapeSpeedZScore);
            else { score = 0; conf = 0.1; }
        }
        return SignalResult.From("TAPE_SPEED", score, conf, now);
    }

    // ── Regime classifier ──────────────────────────────────────────────────────────
    private void UpdateRegime()
    {
        var window = _cb.Window;
        var n = window.Count;
        if (n < AdxPeriod * 3) return;

        // ADX on the window (oldest at the end — same convention as MT5 after ArrayReverse).
        var hi = new double[n]; var lo = new double[n]; var cl = new double[n];
        for (var i = 0; i < n; i++) { hi[i] = window[i].High; lo[i] = window[i].Low; cl[i] = window[i].Close; }
        var adx = ComputeAdx(hi, lo, cl, AdxPeriod);

        // BB width ratio = 4σ / mean(close).
        var closes = new double[n];
        for (var i = 0; i < n; i++) closes[i] = window[i].Close;
        var meanC = Mean(closes, n);
        var sdC = Stdev(closes, n, meanC);
        var bbWidth = meanC > 1e-10 ? 4.0 * sdC / meanC : 0.0;

        // Higher-TF delta proxy: up bars vs down bars over the window.
        int up = 0, dn = 0;
        for (var i = 0; i < n; i++)
        {
            if (window[i].Close > window[i].Open) up++;
            else if (window[i].Close < window[i].Open) dn++;
        }
        var htfDelta = up > dn ? 1 : dn > up ? -1 : 0;

        RegimeKind kind;
        if (adx > AdxTrendingThreshold && bbWidth > BbWidthExpanding)
            kind = htfDelta >= 0 ? RegimeKind.TrendingBull : RegimeKind.TrendingBear;
        else if (adx < AdxRangingThreshold && bbWidth < BbWidthExpanding)
            kind = RegimeKind.Ranging;
        else if (bbWidth > BbWidthExpanding * 3.0)
            kind = RegimeKind.HighVolatility;
        else
            kind = RegimeKind.Undefined;

        _regime = kind;
    }

    // ── Risk management ────────────────────────────────────────────────────────────
    private void UpdateRiskState(Tick tick, double mid)
    {
        var today = tick.TimestampUtc.Date;
        if (_lastDay != today) { _lastDay = today; _dailyPnl = 0; }

        var floating = _position == 0
            ? 0.0
            : _position > 0 ? (mid - _entryPrice) * _position : (_entryPrice - mid) * Math.Abs(_position);
        var equity = _balance + floating;
        if (equity > _peakEquity) _peakEquity = equity;

        if (_balance > 0 && MaxDailyLossPercent > 0)
        {
            var dailyLossPct = -(_dailyPnl + floating) / _balance * 100.0;
            if (dailyLossPct >= MaxDailyLossPercent) _killSwitch = true;
        }
        if (_peakEquity > 0 && MaxDrawdownPercent > 0)
        {
            var ddPct = (_peakEquity - equity) / _peakEquity * 100.0;
            if (ddPct >= MaxDrawdownPercent) _killSwitch = true;
        }
    }

    private long DynamicQuantity(double slDistPriceUnits)
    {
        // qty ≈ (balance * risk%) / SL_distance. For non-FX symbols the contract multiplier
        // should fold into the SL distance; here we use a price-units approximation that
        // matches how the backtest engine values P&L (1:1 with price units).
        var riskCash = _balance * RiskPercent / 100.0;
        if (slDistPriceUnits <= 0) return FixedQuantity;
        var qty = (long)Math.Floor(riskCash / slDistPriceUnits);
        return Math.Max(1, qty);
    }

    private bool IsSessionAllowed(DateTime nowUtc)
    {
        // Sessions in UTC. Asian 00–09, London 08–17, NY 13–22, overlap 13–17.
        var h = nowUtc.Hour;
        var inAsian = h >= 0 && h < 9;
        var inLondon = h >= 8 && h < 17;
        var inNy = h >= 13 && h < 22;
        var inOverlap = h >= 13 && h < 17;

        if (inOverlap && TradeLondonNy) return true;
        if (inLondon && !inOverlap && TradeLondon) return true;
        if (inNy && !inOverlap && TradeNewYork) return true;
        if (inAsian && TradeAsian) return true;
        return false;
    }

    // ── Stop / target placement ────────────────────────────────────────────────────
    private double ComputeStop(int direction, double entry)
    {
        var fixedSl = SlFixedPriceUnits;
        var buffer = SlBufferPriceUnits;
        var searchRange = fixedSl * 2.0;

        double sl = 0;
        if (SlBehindHvp)
        {
            sl = direction > 0
                ? _vp.NearestHvpBelow(entry) is { } below && entry - below <= searchRange ? below - buffer : 0
                : _vp.NearestHvpAbove(entry) is { } above && above - entry <= searchRange ? above + buffer : 0;
        }
        if (sl <= 0)
            sl = direction > 0 ? entry - fixedSl : entry + fixedSl;
        return sl;
    }

    private double ComputeTarget(int direction, double entry, double slDist)
    {
        double tp = 0;
        if (TpAtOppositeHvp)
        {
            tp = direction > 0
                ? _vp.NearestHvpAbove(entry) ?? 0
                : _vp.NearestHvpBelow(entry) ?? 0;
            if (tp > 0)
            {
                var tpDist = Math.Abs(tp - entry);
                if (tpDist < slDist * TpFixedRr) tp = 0;
            }
        }
        if (tp <= 0)
            tp = direction > 0 ? entry + slDist * TpFixedRr : entry - slDist * TpFixedRr;
        return tp;
    }

    private ApexSnapshot BuildSnapshot(DateTime now, double mid, IReadOnlyList<SignalResult> r, CompositeResult c)
    {
        static ApexSignalScore Pub(SignalResult x) => new(x.Score, x.Confidence, x.Direction, x.IsValid);
        return new ApexSnapshot(
            TimestampUtc: now,
            Mid: mid,
            Delta: Pub(r[0]), Vpin: Pub(r[1]),
            ObiShallow: Pub(r[2]), ObiDeep: Pub(r[3]),
            Footprint: Pub(r[4]), Absorption: Pub(r[5]),
            Hvp: Pub(r[6]), TapeSpeed: Pub(r[7]),
            Composite: c.Score, CompositeDirection: c.Direction,
            SignalsAgree: c.SignalsAgree, SignalsConflict: c.SignalsConflict,
            ConflictFlag: c.ConflictFlag, TradeAllowed: c.TradeAllowed,
            Regime: _regime.ToString(),
            KillSwitch: _killSwitch, DailyPnl: _dailyPnl,
            Balance: _balance, PeakEquity: _peakEquity,
            Position: _position);
    }

    private Task Submit(IOrderRouter router, OrderSide side, long qty, CancellationToken ct) =>
        router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: $"apex-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);

    // ── Math helpers (kept private — duplicates of MathUtils.mqh) ──────────────────
    private static double Mean(double[] arr, int n)
    {
        if (n <= 0) return 0;
        double s = 0; for (var i = 0; i < n; i++) s += arr[i];
        return s / n;
    }

    private static double Stdev(double[] arr, int n, double mean)
    {
        if (n <= 1) return 0;
        double v = 0; for (var i = 0; i < n; i++) { var d = arr[i] - mean; v += d * d; }
        return Math.Sqrt(v / n);
    }

    private static double ZScore(double value, double mean, double sd)
        => sd < 1e-10 ? 0 : (value - mean) / sd;

    private static double ScoreFromZ(double z, double threshold)
        => threshold < 1e-10 ? 0 : Math.Clamp(z / threshold * 3.0, -3.0, 3.0);

    private static double WeightedSlope(double[] x, double[] y, double[] w, int n)
    {
        if (n < 2) return 0;
        double sW = 0, sWx = 0, sWy = 0, sWxx = 0, sWxy = 0;
        for (var i = 0; i < n; i++)
        {
            sW += w[i];
            sWx += w[i] * x[i];
            sWy += w[i] * y[i];
            sWxx += w[i] * x[i] * x[i];
            sWxy += w[i] * x[i] * y[i];
        }
        var denom = sW * sWxx - sWx * sWx;
        return Math.Abs(denom) < 1e-10 ? 0 : (sW * sWxy - sWx * sWy) / denom;
    }

    private static double Rsquared(double[] x, double[] y, int n)
    {
        if (n < 2) return 0;
        // Slope via OLS first.
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (var i = 0; i < n; i++) { sx += x[i]; sy += y[i]; sxx += x[i] * x[i]; sxy += x[i] * y[i]; }
        var denom = n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-10) return 0;
        var slope = (n * sxy - sx * sy) / denom;
        var mx = sx / n; var my = sy / n;
        var intercept = my - slope * mx;
        double ssRes = 0, ssTot = 0;
        for (var i = 0; i < n; i++)
        {
            var predicted = slope * x[i] + intercept;
            var resid = y[i] - predicted;
            ssRes += resid * resid;
            ssTot += (y[i] - my) * (y[i] - my);
        }
        return ssTot < 1e-10 ? 0 : 1.0 - ssRes / ssTot;
    }

    private static double ComputeAdx(double[] high, double[] low, double[] close, int period)
    {
        var n = high.Length;
        if (n < period * 2) return 20.0;
        // window arrays are newest-first (window[0] is the freshest candle).
        var dmp = new double[n]; var dmm = new double[n]; var tr = new double[n];
        for (var i = n - 2; i >= 0; i--)
        {
            // i is newer than i+1.
            var up = high[i] - high[i + 1];
            var dn = low[i + 1] - low[i];
            dmp[i] = up > dn && up > 0 ? up : 0;
            dmm[i] = dn > up && dn > 0 ? dn : 0;
            var hl = high[i] - low[i];
            var hpc = Math.Abs(high[i] - close[i + 1]);
            var lpc = Math.Abs(low[i] - close[i + 1]);
            tr[i] = Math.Max(hl, Math.Max(hpc, lpc));
        }
        var sP = WilderSmooth(dmp, period, n);
        var sM = WilderSmooth(dmm, period, n);
        var sT = WilderSmooth(tr, period, n);
        if (sT[0] < 1e-10) return 20.0;
        var dx = new double[n];
        for (var i = 0; i < n - period; i++)
        {
            var dip = sT[i] > 1e-10 ? 100.0 * sP[i] / sT[i] : 0;
            var dim = sT[i] > 1e-10 ? 100.0 * sM[i] / sT[i] : 0;
            dx[i] = dip + dim > 1e-10 ? 100.0 * Math.Abs(dip - dim) / (dip + dim) : 0;
        }
        var adx = WilderSmooth(dx, period, n);
        return adx[0];
    }

    private static double[] WilderSmooth(double[] raw, int period, int n)
    {
        var smooth = new double[n];
        if (n < period) return smooth;
        // Seed = simple sum of the oldest `period` values.
        double seed = 0;
        for (var i = n - period; i < n; i++) seed += raw[i];
        smooth[n - period] = seed;
        for (var i = n - period - 1; i >= 0; i--)
            smooth[i] = smooth[i + 1] - smooth[i + 1] / period + raw[i];
        return smooth;
    }

    // ── Internal records ───────────────────────────────────────────────────────────
    private enum RegimeKind { Undefined, TrendingBull, TrendingBear, Ranging, HighVolatility }

    private sealed record SignalResult(string Name, double Score, double Confidence, int Direction, bool IsValid, DateTime GeneratedAt)
    {
        public static SignalResult Invalid(string name, DateTime now) => new(name, 0, 0, 0, false, now);
        public static SignalResult From(string name, double score, double confidence, DateTime now)
        {
            var dir = score > 0.05 ? 1 : score < -0.05 ? -1 : 0;
            return new(name, score, confidence, dir, true, now);
        }
    }

    private sealed record CompositeResult(
        double Score, int Direction, int SignalsAgree, int SignalsConflict,
        bool ConflictFlag, bool TradeAllowed, double Confidence);

    // ── Data-layer types (kept internal to the strategy) ───────────────────────────

    private sealed record ClassifiedTick(DateTime Time, double Bid, double Ask, double Mid, long Volume, int Direction, double Spread);

    private sealed class EnrichedCandle
    {
        public DateTime OpenTime;
        public double Open, High, Low, Close;
        public long Volume, BuyVolume, SellVolume;
        public long TickDelta => BuyVolume - SellVolume;
        public double DeltaEfficiency => Volume == 0 ? 0 : (double)Math.Abs(TickDelta) / Volume;
    }

    private sealed class FootprintRow
    {
        public double Price;
        public long BidVolume;   // sell-initiated
        public long AskVolume;   // buy-initiated
        public bool BidImbalance;
        public bool AskImbalance;
        public bool ZeroBid => BidVolume == 0;
        public bool ZeroAsk => AskVolume == 0;
    }

    private sealed class FootprintCandle
    {
        public DateTime Time;
        public readonly List<FootprintRow> Rows = new();
        public int StackedBull;   // max consecutive zero-bid (full ask absorption)
        public int StackedBear;   // max consecutive zero-ask (full bid absorption)
        public double PocPrice;
        public long PocVolume;

        public void Finalise(double ratio)
        {
            // Compute imbalances + stacked counts. Mirrors the MT5 FootprintBuilder Close().
            for (var i = 0; i < Rows.Count; i++)
            {
                var r = Rows[i];
                r.BidImbalance = r.BidVolume > r.AskVolume * ratio;
                r.AskImbalance = r.AskVolume > r.BidVolume * ratio;
                var total = r.BidVolume + r.AskVolume;
                if (total > PocVolume) { PocVolume = total; PocPrice = r.Price; }
            }
            int run = 0;
            foreach (var r in Rows) { if (r.ZeroBid) { run++; if (run > StackedBull) StackedBull = run; } else run = 0; }
            run = 0;
            foreach (var r in Rows) { if (r.ZeroAsk) { run++; if (run > StackedBear) StackedBear = run; } else run = 0; }
        }
    }

    private sealed class TickCollector
    {
        private readonly Queue<ClassifiedTick> _ring;
        private readonly int _capacity;
        private double _prevMid;

        public TickCollector(int capacity)
        {
            _capacity = capacity;
            _ring = new Queue<ClassifiedTick>(capacity);
        }

        public bool IsReady => _ring.Count > 5;

        public ClassifiedTick Push(Tick tick)
        {
            var mid = (tick.Bid + tick.Ask) * 0.5;
            var dir = 0;
            if (_prevMid > 0)
                dir = mid > _prevMid ? 1 : mid < _prevMid ? -1 : 0;
            _prevMid = mid;
            // Volume proxy: aggregate L1 size, same convention as OrderFlowToxicityStrategy.
            var vol = Math.Max(tick.BidSize + tick.AskSize, 1);
            var ct = new ClassifiedTick(tick.TimestampUtc, tick.Bid, tick.Ask, mid, vol, dir, tick.Ask - tick.Bid);
            _ring.Enqueue(ct);
            while (_ring.Count > _capacity) _ring.Dequeue();
            return ct;
        }

        public double TapeSpeed(int windowSec)
        {
            if (_ring.Count == 0) return 0;
            var newest = _ring.Last().Time;
            var threshold = newest - TimeSpan.FromSeconds(windowSec);
            var count = 0;
            foreach (var t in _ring) if (t.Time >= threshold) count++;
            return windowSec <= 0 ? 0 : (double)count / windowSec;
        }

        public double DirectionalFraction(int windowSec)
        {
            if (_ring.Count == 0) return 0.5;
            var newest = _ring.Last().Time;
            var threshold = newest - TimeSpan.FromSeconds(windowSec);
            int up = 0, total = 0;
            foreach (var t in _ring)
            {
                if (t.Time < threshold) continue;
                total++;
                if (t.Direction > 0) up++;
            }
            return total == 0 ? 0.5 : (double)up / total;
        }
    }

    private sealed class CandleBuilder
    {
        private readonly TimeSpan _interval;
        private readonly int _maxWindow;
        private readonly LinkedList<EnrichedCandle> _completed = new();
        private EnrichedCandle? _current;
        public DateTime CurrentBarStart { get; private set; } = DateTime.MinValue;

        public CandleBuilder(int maxWindow, TimeSpan interval)
        {
            _maxWindow = maxWindow;
            _interval = interval;
        }

        public int CompletedCount => _completed.Count;

        /// <summary>Completed candles, newest-first.</summary>
        public IReadOnlyList<EnrichedCandle> Window
        {
            get
            {
                var list = new List<EnrichedCandle>(_completed.Count);
                for (var node = _completed.Last; node is not null; node = node.Previous) list.Add(node.Value);
                return list;
            }
        }

        public (bool rolled, EnrichedCandle? lastCompleted) Push(ClassifiedTick ct)
        {
            var bucket = new DateTime(ct.Time.Ticks - ct.Time.Ticks % _interval.Ticks, DateTimeKind.Utc);
            EnrichedCandle? rolledCandle = null;
            var rolled = false;

            if (_current is null || bucket != CurrentBarStart)
            {
                if (_current is not null)
                {
                    _completed.AddLast(_current);
                    while (_completed.Count > _maxWindow) _completed.RemoveFirst();
                    rolledCandle = _current;
                    rolled = true;
                }
                _current = new EnrichedCandle { OpenTime = bucket, Open = ct.Mid, High = ct.Mid, Low = ct.Mid, Close = ct.Mid };
                CurrentBarStart = bucket;
            }
            if (ct.Mid > _current.High) _current.High = ct.Mid;
            if (ct.Mid < _current.Low) _current.Low = ct.Mid;
            _current.Close = ct.Mid;
            _current.Volume += ct.Volume;
            if (ct.Direction > 0) _current.BuyVolume += ct.Volume;
            else if (ct.Direction < 0) _current.SellVolume += ct.Volume;

            return (rolled, rolledCandle);
        }
    }

    private sealed class FootprintBuilder
    {
        private readonly int _maxWindow;
        private readonly LinkedList<FootprintCandle> _completed = new();
        private FootprintCandle? _current;
        private DateTime _currentTime = DateTime.MinValue;
        public FootprintCandle Live => _current ??= new FootprintCandle { Time = DateTime.MinValue };

        public FootprintBuilder(int maxWindow) { _maxWindow = maxWindow; }

        public FootprintCandle? LastCompleted => _completed.Last?.Value;

        /// <summary>Completed footprints, newest-first.</summary>
        public IReadOnlyList<FootprintCandle> Completed
        {
            get
            {
                var list = new List<FootprintCandle>(_completed.Count);
                for (var node = _completed.Last; node is not null; node = node.Previous) list.Add(node.Value);
                return list;
            }
        }

        public void Push(ClassifiedTick ct, DateTime barStart)
        {
            if (_current is null || _currentTime != barStart)
            {
                if (_current is not null)
                {
                    _completed.AddLast(_current);
                    while (_completed.Count > _maxWindow) _completed.RemoveFirst();
                }
                _current = new FootprintCandle { Time = barStart };
                _currentTime = barStart;
            }
            var price = Math.Round(ct.Mid, 5);
            var row = _current.Rows.FirstOrDefault(r => Math.Abs(r.Price - price) < 1e-7);
            if (row is null) { row = new FootprintRow { Price = price }; _current.Rows.Add(row); }
            if (ct.Direction > 0) row.AskVolume += ct.Volume;
            else if (ct.Direction < 0) row.BidVolume += ct.Volume;
        }

        /// <summary>Finalise imbalance / stacked counts on the most recently completed candle.</summary>
        public void CompleteLast()
        {
            if (_completed.Last is { Value: { } last }) last.Finalise(ratio: 3.0);
        }
    }

    private sealed class VolumeProfile
    {
        private readonly int _maxNodes;
        private readonly SortedDictionary<double, NodeAccum> _nodes = new();
        private int _candlesAdded;

        public VolumeProfile(int maxNodes) { _maxNodes = maxNodes; }

        public bool IsReady => _candlesAdded >= 5;

        public void AddCandle(EnrichedCandle candle, FootprintCandle? footprint)
        {
            _candlesAdded++;
            if (footprint is null) return;
            foreach (var row in footprint.Rows)
            {
                if (!_nodes.TryGetValue(row.Price, out var n))
                {
                    if (_nodes.Count >= _maxNodes) break;
                    n = new NodeAccum { Price = row.Price, BarIndex = _candlesAdded };
                    _nodes[row.Price] = n;
                }
                n.Volume += row.AskVolume + row.BidVolume;
                n.BuyVolume += row.AskVolume;
                n.SellVolume += row.BidVolume;
                n.LastBarIndex = _candlesAdded;
            }
        }

        public IReadOnlyList<HvpNode> Hvps(double stdDevMultiplier)
        {
            if (_nodes.Count == 0) return Array.Empty<HvpNode>();
            var vols = _nodes.Values.Select(n => (double)n.Volume).ToArray();
            var mean = vols.Length == 0 ? 0 : vols.Average();
            var variance = vols.Length <= 1 ? 0 : vols.Average(v => (v - mean) * (v - mean));
            var sd = Math.Sqrt(variance);
            var threshold = mean + stdDevMultiplier * sd;
            var hvps = new List<HvpNode>();
            foreach (var n in _nodes.Values)
            {
                if (n.Volume < threshold) continue;
                hvps.Add(new HvpNode(
                    Price: n.Price,
                    Volume: n.Volume,
                    BarFromOldest: n.BarIndex,
                    BarFromNewest: Math.Max(0, _candlesAdded - n.LastBarIndex)));
            }
            return hvps;
        }

        public bool NearHvp(double price, double buffer)
        {
            foreach (var hvp in Hvps(stdDevMultiplier: 1.5))
                if (Math.Abs(hvp.Price - price) <= buffer) return true;
            return false;
        }

        public double? NearestHvpBelow(double price)
        {
            double? best = null;
            foreach (var hvp in Hvps(stdDevMultiplier: 1.5))
                if (hvp.Price < price && (best is null || hvp.Price > best)) best = hvp.Price;
            return best;
        }

        public double? NearestHvpAbove(double price)
        {
            double? best = null;
            foreach (var hvp in Hvps(stdDevMultiplier: 1.5))
                if (hvp.Price > price && (best is null || hvp.Price < best)) best = hvp.Price;
            return best;
        }

        private sealed class NodeAccum
        {
            public double Price;
            public long Volume;
            public long BuyVolume;
            public long SellVolume;
            public int BarIndex;
            public int LastBarIndex;
        }
    }

    private readonly record struct HvpNode(double Price, long Volume, int BarFromOldest, int BarFromNewest);

    private sealed class VpinAccumulator
    {
        private readonly Queue<Bucket> _buckets;
        private readonly int _maxBuckets;
        private long _bucketSize;
        private long _accumBuy;
        private long _accumSell;
        private long _accumTotal;

        public VpinAccumulator(int maxBuckets, long bucketVolume)
        {
            _maxBuckets = maxBuckets;
            _bucketSize = bucketVolume;
            _buckets = new Queue<Bucket>(maxBuckets);
        }

        public int Count => _buckets.Count;
        public Bucket Latest => _buckets.LastOrDefault();

        public void Push(ClassifiedTick ct)
        {
            _accumTotal += ct.Volume;
            if (ct.Direction > 0) _accumBuy += ct.Volume;
            else if (ct.Direction < 0) _accumSell += ct.Volume;
            while (_accumTotal >= _bucketSize)
            {
                var b = new Bucket(_accumBuy, _accumSell, _accumTotal, _accumTotal == 0 ? 0.5 : (double)_accumBuy / _accumTotal);
                _buckets.Enqueue(b);
                while (_buckets.Count > _maxBuckets) _buckets.Dequeue();
                _accumBuy = _accumSell = _accumTotal = 0;
            }
        }

        public double AverageToxicity(int lookback)
        {
            if (_buckets.Count == 0) return 0;
            var take = Math.Min(lookback, _buckets.Count);
            var arr = _buckets.Reverse().Take(take).ToArray();
            double sum = 0;
            foreach (var b in arr) sum += Math.Abs(b.BuyFraction - 0.5) * 2.0;
            return sum / arr.Length;
        }

        public bool IsRising()
        {
            if (_buckets.Count < 3) return false;
            var arr = _buckets.Reverse().Take(3).ToArray();
            var t0 = Math.Abs(arr[0].BuyFraction - 0.5) * 2.0;
            var t2 = Math.Abs(arr[2].BuyFraction - 0.5) * 2.0;
            return t0 > t2;
        }

        internal readonly record struct Bucket(long Buy, long Sell, long Total, double BuyFraction);
    }

    private sealed class RingBuffer<T>
    {
        private readonly Queue<T> _q;
        private readonly int _capacity;
        public RingBuffer(int capacity) { _capacity = capacity; _q = new Queue<T>(capacity); }
        public int Count => _q.Count;
        public void Push(T v) { _q.Enqueue(v); while (_q.Count > _capacity) _q.Dequeue(); }
        public T[] ToArray() => _q.ToArray();
    }
}
