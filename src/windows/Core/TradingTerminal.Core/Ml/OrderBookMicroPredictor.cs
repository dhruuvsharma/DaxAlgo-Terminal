namespace TradingTerminal.Core.Ml;

/// <summary>
/// Online order-book micro-forecaster: learns, walk-forward, to predict the microprice move (in
/// ticks) at several direct horizons plus the probability of three liquidity events — spread
/// widening, top-of-book depth draining, sweep-cost jumping — over a short lookahead window.
/// Stepped once per fixed cadence sample (the window's 250 ms capture tick, or the warm-start
/// resampler's boundaries), it first scores and learns every pending forecast whose target step
/// just realized, then forecasts again from the updated state.
///
/// <para>Built on the same primitives as <see cref="FootprintNextBarPredictor"/>: a bank of
/// <see cref="OnlineLinearRegression"/> learners (one per direction horizon + one per event; RLS
/// with exponential forgetting), features standardized by an <see cref="OnlineFeatureScaler"/>,
/// direction accuracy tracked by <see cref="RollingForecastMetrics"/> against the naive
/// queue-imbalance rule on identical realized steps, and event calibration tracked by
/// <see cref="RollingBrierScore"/>. Event learners are linear probability models — predictions
/// clamp to [0, 1].</para>
///
/// <para>The tick size is estimated internally (running minimum of the positive adjacent level
/// gaps each step reports); every pending forecast pins the tick it was created with, so a
/// mid-run refinement never corrupts already-queued targets. Deterministic, single-threaded
/// (caller confines), fixed-size state.</para>
/// </summary>
public sealed class OrderBookMicroPredictor
{
    private const int FeatureDim = 19;
    private const double EwmaHalfLifeSteps = 64.0;
    private const double DefaultTick = 0.01;
    private const int FeatureLagSteps = 5;

    private readonly OrderBookPredictorOptions _options;
    private readonly int _flagshipIndex;
    private readonly double _ewmaAlpha;
    private readonly List<OrderBookStepSummary> _ring;
    private readonly List<PendingDirection> _pendingDirections = new();
    private readonly List<PendingEvents> _pendingEvents = new();
    private readonly RollingForecastMetrics _mlMetrics;
    private readonly RollingForecastMetrics _baselineMetrics;
    private readonly RollingBrierScore _spreadScore;
    private readonly RollingBrierScore _depthScore;
    private readonly RollingBrierScore _sweepScore;

    private OnlineLinearRegression[] _directionBank;
    private OnlineLinearRegression[] _eventBank;
    private OnlineFeatureScaler _scaler;
    private long _stepIndex = -1;
    private long _samplesSeen;
    private double _observedTick = double.MaxValue;
    private bool _ewmaInitialized;
    private double _ewmaTotalDepth;
    private double _ewmaLogDepth;
    private double _ewmaAbsFlow;
    private double _ewmaSignedFlowRatio;
    private double _ewmaLogTradeCount;
    private OrderBookForecast? _lastForecast;

    public OrderBookMicroPredictor(OrderBookPredictorOptions? options = null)
    {
        _options = options ?? new OrderBookPredictorOptions();
        if (_options.Horizons.Count == 0) throw new ArgumentOutOfRangeException(nameof(options), "At least one horizon is required.");
        _flagshipIndex = IndexOfFlagship(_options);
        if (_options.EventWindow <= 0) throw new ArgumentOutOfRangeException(nameof(options), "EventWindow must be positive.");
        if (_options.HistoryCapacity < Math.Max(_options.EventWindow + 1, FeatureLagSteps))
            throw new ArgumentOutOfRangeException(nameof(options), "HistoryCapacity must cover the event window and feature lags.");

        _ewmaAlpha = 1.0 - Math.Pow(2.0, -1.0 / EwmaHalfLifeSteps);
        _ring = new List<OrderBookStepSummary>(_options.HistoryCapacity);
        _mlMetrics = new RollingForecastMetrics(_options.MetricsWindow);
        _baselineMetrics = new RollingForecastMetrics(_options.MetricsWindow);
        _spreadScore = new RollingBrierScore(_options.MetricsWindow);
        _depthScore = new RollingBrierScore(_options.MetricsWindow);
        _sweepScore = new RollingBrierScore(_options.MetricsWindow);
        _directionBank = CreateBank(_options.Horizons.Count);
        _eventBank = CreateBank(3);
        _scaler = new OnlineFeatureScaler(FeatureDim);
    }

    private static int IndexOfFlagship(OrderBookPredictorOptions options)
    {
        for (var i = 0; i < options.Horizons.Count; i++)
            if (options.Horizons[i] == options.FlagshipHorizon) return i;
        throw new ArgumentOutOfRangeException(nameof(options), "FlagshipHorizon must be one of Horizons.");
    }

    /// <summary>Rolling flagship-horizon accuracy of the model's microprice forecasts (only
    /// forecasts made after the model was ready are scored).</summary>
    public ForecastAccuracy MlAccuracy => _mlMetrics.Snapshot();

    /// <summary>Rolling flagship-horizon accuracy of the queue-imbalance rule, scored on the same
    /// realized steps. Its MAE is shown for completeness; the hit-rate is the meaningful column
    /// (the rule only ever calls direction, sized at the half-spread).</summary>
    public ForecastAccuracy BaselineAccuracy => _baselineMetrics.Snapshot();

    public EventScore SpreadWidenScore => _spreadScore.Snapshot();
    public EventScore DepthDrainScore => _depthScore.Snapshot();
    public EventScore SweepJumpScore => _sweepScore.Snapshot();

    /// <summary>Steps folded into the engine so far (readiness read-out).</summary>
    public long SamplesSeen => _samplesSeen;

    /// <summary>True once the flagship direction learner has absorbed at least
    /// <see cref="OrderBookPredictorOptions.MinSamplesReady"/> updates (≈ 10 s of live steps).</summary>
    public bool IsReady => _directionBank[_flagshipIndex].Samples >= _options.MinSamplesReady;

    /// <summary>The current tick-size estimate: the smallest positive adjacent-level gap observed
    /// so far, defaulting to 0.01 until the first observation.</summary>
    public double TickSize => _observedTick == double.MaxValue ? DefaultTick : _observedTick;

    /// <summary>The forecast produced by the most recent step — null while warming up or when the
    /// book was unusable. Lets the owner re-publish without waiting for the next step.</summary>
    public OrderBookForecast? LastForecast => _lastForecast;

    /// <summary>
    /// Feeds one step: refines the tick estimate, scores + learns every pending forecast whose
    /// target step just realized, then predicts the microprice path and event probabilities from
    /// the updated state. Returns null until the model has enough usable history and is ready.
    /// </summary>
    public OrderBookForecast? OnStep(OrderBookStepSummary step)
    {
        _stepIndex++;
        _samplesSeen++;
        if (step.MinLevelGap > 0)
            _observedTick = Math.Min(_observedTick, Math.Max(step.MinLevelGap, 1e-9));

        var usable = IsUsable(step);
        LearnRealizedDirections(step, usable);
        LearnRealizedEvents(step, usable);

        _ring.Add(step);
        if (_ring.Count > _options.HistoryCapacity) _ring.RemoveAt(0);
        if (usable) UpdateEwmas(step);

        if (!usable || !TryBuildRawFeatures(out var raw))
        {
            _lastForecast = null;
            return null;
        }

        var scaled = new double[FeatureDim];
        _scaler.Transform(raw, scaled);
        _scaler.Observe(raw);

        var wasReady = IsReady;
        var tick = TickSize;
        var directionPredictions = new double[_options.Horizons.Count];
        for (var i = 0; i < _options.Horizons.Count; i++)
            directionPredictions[i] = _directionBank[i].Predict(scaled);
        var pSpread = Math.Clamp(_eventBank[0].Predict(scaled), 0.0, 1.0);
        var pDepth = Math.Clamp(_eventBank[1].Predict(scaled), 0.0, 1.0);
        var pSweep = Math.Clamp(_eventBank[2].Predict(scaled), 0.0, 1.0);

        var halfSpreadTicks = (step.BestAsk - step.BestBid) * 0.5 / tick;
        var baselineDeltaTicks = Math.Abs(step.ImbalanceL1) < _options.BaselineDeadBand
            ? 0.0
            : Math.Sign(step.ImbalanceL1) * halfSpreadTicks;

        for (var i = 0; i < _options.Horizons.Count; i++)
        {
            _pendingDirections.Add(new PendingDirection(
                TargetIndex: _stepIndex + _options.Horizons[i],
                HorizonIndex: i,
                Features: scaled,
                ReferenceMicroprice: step.Microprice,
                Tick: tick,
                MlDeltaTicks: directionPredictions[i],
                BaselineDeltaTicks: i == _flagshipIndex ? baselineDeltaTicks : double.NaN,
                ScoreMl: wasReady));
        }
        _pendingEvents.Add(new PendingEvents(
            TargetIndex: _stepIndex + _options.EventWindow,
            Features: scaled,
            ReferenceSpread: step.BestAsk - step.BestBid,
            ReferenceBid3: step.BidDepthTop3,
            ReferenceAsk3: step.AskDepthTop3,
            ReferenceWorstSweep: Math.Max(step.SweepCostBuy, step.SweepCostSell),
            Tick: tick,
            PSpread: pSpread,
            PDepth: pDepth,
            PSweep: pSweep,
            ScoreEvents: wasReady));

        if (!wasReady)
        {
            _lastForecast = null;
            return null;
        }

        var path = new MicropricePoint[_options.Horizons.Count];
        for (var i = 0; i < _options.Horizons.Count; i++)
            path[i] = new MicropricePoint(_options.Horizons[i], step.Microprice + directionPredictions[i] * tick);
        _lastForecast = new OrderBookForecast(step.TimestampUtc, step.Microprice, tick, path, pSpread, pDepth, pSweep);
        return _lastForecast;
    }

    public void Reset()
    {
        _directionBank = CreateBank(_options.Horizons.Count);
        _eventBank = CreateBank(3);
        _scaler = new OnlineFeatureScaler(FeatureDim);
        _ring.Clear();
        _pendingDirections.Clear();
        _pendingEvents.Clear();
        _mlMetrics.Reset();
        _baselineMetrics.Reset();
        _spreadScore.Reset();
        _depthScore.Reset();
        _sweepScore.Reset();
        _stepIndex = -1;
        _samplesSeen = 0;
        _observedTick = double.MaxValue;
        _ewmaInitialized = false;
        _ewmaTotalDepth = 0;
        _ewmaLogDepth = 0;
        _ewmaAbsFlow = 0;
        _ewmaSignedFlowRatio = 0;
        _ewmaLogTradeCount = 0;
        _lastForecast = null;
    }

    private OnlineLinearRegression[] CreateBank(int count)
    {
        var bank = new OnlineLinearRegression[count];
        for (var i = 0; i < count; i++) bank[i] = new OnlineLinearRegression(FeatureDim, _options.Lambda);
        return bank;
    }

    /// <summary>A step is unusable when the book is empty/degenerate on either side — it stays in
    /// the ring for time-keeping, but produces no learning signal and drops the pendings whose
    /// targets or windows it falls in.</summary>
    private static bool IsUsable(OrderBookStepSummary step) =>
        double.IsFinite(step.BestBid) && double.IsFinite(step.BestAsk) &&
        step.BestBid > 0 && step.BestAsk >= step.BestBid &&
        double.IsFinite(step.Microprice) && step.Microprice > 0;

    private void LearnRealizedDirections(OrderBookStepSummary realized, bool realizedUsable)
    {
        for (var i = _pendingDirections.Count - 1; i >= 0; i--)
        {
            var p = _pendingDirections[i];
            if (p.TargetIndex > _stepIndex) continue;
            _pendingDirections.RemoveAt(i);
            if (p.TargetIndex < _stepIndex || !realizedUsable) continue;

            var realizedDeltaTicks = (realized.Microprice - p.ReferenceMicroprice) / p.Tick;
            _directionBank[p.HorizonIndex].Update(p.Features, realizedDeltaTicks);

            if (p.HorizonIndex != _flagshipIndex) continue;
            if (p.ScoreMl) _mlMetrics.Score(p.MlDeltaTicks, realizedDeltaTicks);
            _baselineMetrics.Score(p.BaselineDeltaTicks, realizedDeltaTicks);
        }
    }

    /// <summary>Realizes event pendings whose window just closed: aggregates max spread / min
    /// side depth / max worst-sweep over the window's steps, labels via
    /// <see cref="OrderBookEventLabeler"/>, and learns. A window containing any unusable step is
    /// dropped — a dead book must not be read as "the spread never widened".</summary>
    private void LearnRealizedEvents(OrderBookStepSummary realized, bool realizedUsable)
    {
        for (var i = _pendingEvents.Count - 1; i >= 0; i--)
        {
            var p = _pendingEvents[i];
            if (p.TargetIndex > _stepIndex) continue;
            _pendingEvents.RemoveAt(i);
            if (p.TargetIndex < _stepIndex || !realizedUsable) continue;
            if (!TryAggregateWindow(realized, out var maxSpread, out var minBid3, out var minAsk3, out var maxWorstSweep))
                continue;

            var spreadWidened = OrderBookEventLabeler.SpreadWidened(p.ReferenceSpread, maxSpread, p.Tick, _options.SpreadWidenTicks);
            var depthDrained = OrderBookEventLabeler.DepthDrained(p.ReferenceBid3, p.ReferenceAsk3, minBid3, minAsk3, _options.DepthDrainRatio);
            var sweepJumped = OrderBookEventLabeler.SweepJumped(p.ReferenceWorstSweep, maxWorstSweep, p.Tick, _options.SweepJumpRatio);

            _eventBank[0].Update(p.Features, spreadWidened ? 1.0 : 0.0);
            _eventBank[1].Update(p.Features, depthDrained ? 1.0 : 0.0);
            _eventBank[2].Update(p.Features, sweepJumped ? 1.0 : 0.0);

            if (!p.ScoreEvents) continue;
            _spreadScore.Score(p.PSpread, spreadWidened);
            _depthScore.Score(p.PDepth, depthDrained);
            _sweepScore.Score(p.PSweep, sweepJumped);
        }
    }

    /// <summary>Aggregates max spread / min top-3 depth / max worst-sweep over the event window —
    /// the W steps after the reference: the realizing step (not yet in the ring when this runs)
    /// plus the newest W − 1 ring entries. False when the window is short or contains an unusable
    /// step (the pending is dropped rather than mislabeled).</summary>
    private bool TryAggregateWindow(OrderBookStepSummary realized,
        out double maxSpread, out long minBid3, out long minAsk3, out double maxWorstSweep)
    {
        maxSpread = realized.BestAsk - realized.BestBid;
        minBid3 = realized.BidDepthTop3;
        minAsk3 = realized.AskDepthTop3;
        maxWorstSweep = Math.Max(realized.SweepCostBuy, realized.SweepCostSell);

        var needed = _options.EventWindow - 1;
        if (needed <= 0) return true;
        if (_ring.Count < needed) return false;

        for (var i = _ring.Count - needed; i < _ring.Count; i++)
        {
            var s = _ring[i];
            if (!IsUsable(s)) return false;
            var spread = s.BestAsk - s.BestBid;
            if (spread > maxSpread) maxSpread = spread;
            if (s.BidDepthTop3 < minBid3) minBid3 = s.BidDepthTop3;
            if (s.AskDepthTop3 < minAsk3) minAsk3 = s.AskDepthTop3;
            var worst = Math.Max(s.SweepCostBuy, s.SweepCostSell);
            if (worst > maxWorstSweep) maxWorstSweep = worst;
        }
        return true;
    }

    private void UpdateEwmas(OrderBookStepSummary step)
    {
        var totalDepth = (double)(step.BidDepthTopN + step.AskDepthTopN);
        var logDepth = Math.Log(1.0 + totalDepth);
        var absFlow = Math.Abs((double)step.SignedTradeFlow);
        var logTrades = Math.Log(1.0 + step.TradeCount);

        if (!_ewmaInitialized)
        {
            _ewmaTotalDepth = totalDepth;
            _ewmaLogDepth = logDepth;
            _ewmaAbsFlow = absFlow;
            _ewmaSignedFlowRatio = 0;
            _ewmaLogTradeCount = logTrades;
            _ewmaInitialized = true;
        }
        else
        {
            _ewmaTotalDepth += _ewmaAlpha * (totalDepth - _ewmaTotalDepth);
            _ewmaLogDepth += _ewmaAlpha * (logDepth - _ewmaLogDepth);
            _ewmaAbsFlow += _ewmaAlpha * (absFlow - _ewmaAbsFlow);
            _ewmaLogTradeCount += _ewmaAlpha * (logTrades - _ewmaLogTradeCount);
        }

        if (step.TradeFlowValid)
        {
            var flowRatio = step.SignedTradeFlow / Math.Max(1.0, _ewmaAbsFlow);
            _ewmaSignedFlowRatio += _ewmaAlpha * (flowRatio - _ewmaSignedFlowRatio);
        }
    }

    /// <summary>Builds the raw feature vector from the newest <see cref="FeatureLagSteps"/> ring
    /// entries. False when the lag window is short or holds an unusable step.</summary>
    private bool TryBuildRawFeatures(out double[] raw)
    {
        raw = Array.Empty<double>();
        var n = _ring.Count;
        if (n < FeatureLagSteps) return false;
        for (var i = 1; i <= FeatureLagSteps; i++)
            if (!IsUsable(_ring[n - i])) return false;

        var step = _ring[n - 1];
        var prior1 = _ring[n - 2];
        var prior2 = _ring[n - 3];
        var prior4 = _ring[n - 5];
        var tick = TickSize;

        var mid = (step.BestBid + step.BestAsk) * 0.5;
        var flowRatio = step.TradeFlowValid ? step.SignedTradeFlow / Math.Max(1.0, _ewmaAbsFlow) : 0.0;

        raw = new double[FeatureDim]
        {
            1.0,
            (step.Microprice - prior1.Microprice) / tick,
            (prior1.Microprice - prior2.Microprice) / tick,
            (step.Microprice - prior4.Microprice) / tick,
            step.ImbalanceL1,
            step.ImbalanceCum,
            (step.Microprice - mid) / tick,
            (step.BestAsk - step.BestBid) / tick,
            Math.Log((1.0 + step.BidDepthTopN) / (1.0 + step.AskDepthTopN)),
            Math.Log(1.0 + step.BidDepthTopN + step.AskDepthTopN) - _ewmaLogDepth,
            (step.BidDepthTop3 - prior1.BidDepthTop3) / Math.Max(1.0, _ewmaTotalDepth),
            (step.AskDepthTop3 - prior1.AskDepthTop3) / Math.Max(1.0, _ewmaTotalDepth),
            Math.Max(step.LargestGapBid, step.LargestGapAsk) / tick,
            Math.Max(step.SweepCostBuy, step.SweepCostSell) / tick,
            (step.SweepCostBuy - step.SweepCostSell) / tick,
            flowRatio,
            _ewmaSignedFlowRatio,
            step.TradeFlowValid ? Math.Log(1.0 + step.TradeCount) - _ewmaLogTradeCount : 0.0,
            step.TradeFlowValid ? 1.0 : 0.0,
        };
        return true;
    }

    /// <summary>One outstanding direction forecast. The scaled feature vector is retained so the
    /// learner updates against exactly what it predicted from; the tick is pinned at creation.</summary>
    private sealed record PendingDirection(
        long TargetIndex,
        int HorizonIndex,
        double[] Features,
        double ReferenceMicroprice,
        double Tick,
        double MlDeltaTicks,
        double BaselineDeltaTicks,
        bool ScoreMl);

    /// <summary>One outstanding event-window forecast (all three events share the window).</summary>
    private sealed record PendingEvents(
        long TargetIndex,
        double[] Features,
        double ReferenceSpread,
        long ReferenceBid3,
        long ReferenceAsk3,
        double ReferenceWorstSweep,
        double Tick,
        double PSpread,
        double PDepth,
        double PSweep,
        bool ScoreEvents);
}
