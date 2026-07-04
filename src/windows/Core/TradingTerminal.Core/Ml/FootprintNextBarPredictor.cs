using System.Text.Json;

namespace TradingTerminal.Core.Ml;

/// <summary>
/// Online next-bar footprint forecaster: learns to predict the next bars' total/buy/sell POC,
/// total volume and delta from the sealed-bar footprint features, walk-forward, with no offline
/// training step. Built on a bank of <see cref="OnlineLinearRegression"/> learners (RLS with
/// exponential forgetting) — one per (target × horizon) so an outlier in one target's residual
/// cannot destabilize another target's coefficients.
///
/// <para>Horizons are <em>direct</em>: learner<sub>h</sub> fits <c>target(t+h) − reference(t)</c>
/// from the features at t, rather than iterating a 1-step model (which would need synthetic
/// future feature vectors and compound its own error).</para>
///
/// <para>Feeding order inside <see cref="OnBarSealed"/> is fixed — score/learn every pending
/// snapshot whose target bar just realized, append the bar, update the EWMAs, build + scale the
/// feature vector, predict, queue new pendings — so two engines fed identical sequences emit
/// identical forecasts. All price targets are POC-relative in ticks (stationary); volume is
/// learned in log space relative to its EWMA; delta is volume-normalized and clamped.</para>
///
/// Pure C#, deterministic, single-threaded (the caller confines it to one thread).
/// </summary>
public sealed class FootprintNextBarPredictor
{
    private const int FeatureDim = 16;
    private const int TargetCount = 5;
    private const double EwmaHalfLifeBars = 16.0;
    private const double DeltaClamp = 3.0;
    private const int LagBarsRequired = 4;

    /// <summary>Model-family discriminator this predictor's artifacts are filed under in the registry.</summary>
    public const string ModelKind = "footprint-nextbar";
    private const string BankName = "nextbar";

    /// <summary>Ordered names of the raw feature vector (see <see cref="TryBuildRawFeatures"/>) — the
    /// artifact's <see cref="FeatureContract"/>, which guards restores and documents the inputs.</summary>
    private static readonly string[] FeatureNames =
    {
        "bias", "poc_mom_1", "poc_mom_2", "poc_mom_3", "buy_sell_poc_spread",
        "poc_position_in_range", "value_area_width_ticks", "range_ticks", "delta_ratio",
        "ewma_delta_ratio", "log_volume_vs_ewma", "cum_delta_change", "stacked_imbalance_net",
        "feed_quality", "baseline_poc_delta_ticks", "baseline_valid",
    };

    private readonly double _tickSize;
    private readonly FootprintPredictorOptions _options;
    private readonly double _ewmaAlpha;
    private readonly RollingForecastMetrics _mlMetrics;
    private readonly RollingForecastMetrics _baselineMetrics;
    private readonly List<FootprintBarSummary> _history;
    private readonly List<PendingSnapshot> _pending = new();

    private OnlineLinearRegression[][] _bank;   // [horizon − 1][target]
    private OnlineFeatureScaler _scaler;
    private long _barIndex = -1;
    private long _samplesSeen;
    private bool _ewmaInitialized;
    private double _ewmaVolume;
    private double _ewmaLogVolume;
    private double _ewmaDeltaRatio;
    private FootprintForecastBar[] _lastForecast = Array.Empty<FootprintForecastBar>();

    public FootprintNextBarPredictor(double tickSize, FootprintPredictorOptions? options = null)
    {
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize));
        _tickSize = tickSize;
        _options = options ?? new FootprintPredictorOptions();
        if (_options.MaxHorizon <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxHorizon must be positive.");
        if (_options.HistoryCapacity < LagBarsRequired) throw new ArgumentOutOfRangeException(nameof(options), $"HistoryCapacity must be at least {LagBarsRequired}.");

        _ewmaAlpha = 1.0 - Math.Pow(2.0, -1.0 / EwmaHalfLifeBars);
        _history = new List<FootprintBarSummary>(_options.HistoryCapacity);
        _mlMetrics = new RollingForecastMetrics(_options.MetricsWindow);
        _baselineMetrics = new RollingForecastMetrics(_options.MetricsWindow);
        _bank = CreateBank();
        _scaler = new OnlineFeatureScaler(FeatureDim);
    }

    /// <summary>Rolling 1-step accuracy of this model's POC forecasts (only forecasts made after
    /// the model was ready are scored).</summary>
    public ForecastAccuracy MlAccuracy => _mlMetrics.Snapshot();

    /// <summary>Rolling 1-step accuracy of the supplied baseline (the regression-consensus POC),
    /// scored on the same realized bars whenever the baseline was finite.</summary>
    public ForecastAccuracy BaselineAccuracy => _baselineMetrics.Snapshot();

    /// <summary>Sealed bars folded into the engine so far (readiness read-out).</summary>
    public long SamplesSeen => _samplesSeen;

    /// <summary>True once the 1-step learners have absorbed at least
    /// <see cref="FootprintPredictorOptions.MinSamplesReady"/> updates.</summary>
    public bool IsReady => _bank[0][0].Samples >= _options.MinSamplesReady;

    /// <summary>The forecast emitted by the most recent <see cref="OnBarSealed"/> call (empty
    /// while warming up). Lets the owner re-publish without waiting for the next seal.</summary>
    public IReadOnlyList<FootprintForecastBar> LastForecast => _lastForecast;

    /// <summary>
    /// Feeds one sealed bar: scores and learns every pending prediction whose target bar just
    /// realized, then predicts horizons 1..MaxHorizon from the updated state and returns the
    /// fresh forecast (empty until the model is ready). <paramref name="baselineNextPoc"/> is the
    /// regression-consensus 1-step POC forecast made <em>before</em> this bar's successor — NaN
    /// when unavailable — used both as a meta-feature and to score the baseline.
    /// </summary>
    public IReadOnlyList<FootprintForecastBar> OnBarSealed(FootprintBarSummary bar, double baselineNextPoc)
    {
        _barIndex++;
        _samplesSeen++;
        var barUsable = IsUsable(bar);

        LearnRealizedPendings(bar, barUsable);

        _history.Add(bar);
        if (_history.Count > _options.HistoryCapacity) _history.RemoveAt(0);
        if (barUsable) UpdateEwmas(bar);

        _lastForecast = Array.Empty<FootprintForecastBar>();
        if (!barUsable || !TryBuildRawFeatures(baselineNextPoc, out var raw)) return _lastForecast;

        var scaled = new double[FeatureDim];
        _scaler.Transform(raw, scaled);
        _scaler.Observe(raw);

        var wasReady = IsReady;
        var predictions = new double[_options.MaxHorizon][];
        for (var h = 1; h <= _options.MaxHorizon; h++)
        {
            var learners = _bank[h - 1];
            var y = new double[TargetCount];
            for (var k = 0; k < TargetCount; k++) y[k] = learners[k].Predict(scaled);
            predictions[h - 1] = y;
        }

        var baselineDeltaTicks = double.IsFinite(baselineNextPoc)
            ? (baselineNextPoc - bar.Poc) / _tickSize
            : double.NaN;
        for (var h = 1; h <= _options.MaxHorizon; h++)
        {
            _pending.Add(new PendingSnapshot(
                TargetIndex: _barIndex + h,
                Horizon: h,
                Features: scaled,
                ReferencePoc: bar.Poc,
                EwmaLogVolume: _ewmaLogVolume,
                EwmaVolume: _ewmaVolume,
                MlPocDeltaTicks: predictions[h - 1][0],
                BaselinePocDeltaTicks: h == 1 ? baselineDeltaTicks : double.NaN,
                ScoreMl: wasReady));
        }

        if (wasReady)
        {
            var forecast = new FootprintForecastBar[_options.MaxHorizon];
            for (var h = 1; h <= _options.MaxHorizon; h++)
                forecast[h - 1] = Reconstruct(bar, predictions[h - 1], h);
            _lastForecast = forecast;
        }
        return _lastForecast;
    }

    public void Reset()
    {
        _bank = CreateBank();
        _scaler = new OnlineFeatureScaler(FeatureDim);
        _history.Clear();
        _pending.Clear();
        _mlMetrics.Reset();
        _baselineMetrics.Reset();
        _barIndex = -1;
        _samplesSeen = 0;
        _ewmaInitialized = false;
        _ewmaVolume = 0;
        _ewmaLogVolume = 0;
        _ewmaDeltaRatio = 0;
        _lastForecast = Array.Empty<FootprintForecastBar>();
    }

    /// <summary>
    /// Checkpoints the learned state (the per-(target × horizon) RLS weights, the feature
    /// standardizer, and the running EWMAs) into a portable <see cref="ModelArtifact"/> filed under
    /// the given instrument/timeframe. The transient harness (history ring, pending forecasts) is
    /// deliberately not captured — it re-fills within a few bars, while the artifact preserves
    /// everything that took data to learn.
    /// </summary>
    public ModelArtifact CreateArtifact(string instrumentKey, string timeframe)
    {
        var learners = new List<ForecasterState>(_options.MaxHorizon * TargetCount);
        for (var h = 0; h < _options.MaxHorizon; h++)
            for (var k = 0; k < TargetCount; k++)
                learners.Add(_bank[h][k].SaveState());

        var scalars = new[]
        {
            new ScalarState("ewma_volume", _ewmaVolume),
            new ScalarState("ewma_log_volume", _ewmaLogVolume),
            new ScalarState("ewma_delta_ratio", _ewmaDeltaRatio),
            new ScalarState("ewma_initialized", _ewmaInitialized ? 1.0 : 0.0),
            new ScalarState("tick_size", _tickSize),
        };

        var ml = MlAccuracy;
        var baseline = BaselineAccuracy;
        var metrics = new ModelMetrics(
            ml.PocMaeTicks, ml.DirectionalHitRate, baseline.PocMaeTicks, baseline.DirectionalHitRate, ml.ScoredCount);
        var trainedThrough = _history.Count > 0
            ? DateTime.SpecifyKind(_history[^1].StartUtc, DateTimeKind.Utc)
            : DateTime.UtcNow;

        return new ModelArtifact(
            SchemaVersion: ModelArtifact.CurrentSchemaVersion,
            ModelKind: ModelKind,
            Algorithm: OnlineLinearRegression.ForecasterKind,
            InstrumentKey: instrumentKey,
            Timeframe: timeframe,
            Features: new FeatureContract(FeatureDim, FeatureNames),
            OptionsJson: JsonSerializer.Serialize(_options, ModelArtifactJson.Options),
            Banks: new[] { new BankState(BankName, learners) },
            Scaler: _scaler.SaveState(),
            Scalars: scalars,
            Metrics: metrics,
            SamplesTrained: _samplesSeen,
            TrainedThroughUtc: trainedThrough,
            CreatedUtc: DateTime.UtcNow);
    }

    /// <summary>
    /// Restores a previously-checkpointed model into this predictor so it resumes warm instead of
    /// cold. Returns false (leaving the predictor freshly cold) when the artifact is from a different
    /// model family/algorithm, a mismatched feature contract, or a different bank/scaler shape — a
    /// tuning change must not load stale weights against the wrong feature vector.
    /// </summary>
    public bool TryRestore(ModelArtifact artifact)
    {
        if (artifact.SchemaVersion != ModelArtifact.CurrentSchemaVersion) return false;
        if (artifact.ModelKind != ModelKind) return false;
        if (artifact.Algorithm != OnlineLinearRegression.ForecasterKind) return false;
        if (artifact.Features.Dimension != FeatureDim) return false;
        if (artifact.Scaler.Dimensions != FeatureDim) return false;
        var bank = artifact.Bank(BankName);
        if (bank is null || bank.Learners.Count != _options.MaxHorizon * TargetCount) return false;

        try
        {
            var idx = 0;
            for (var h = 0; h < _options.MaxHorizon; h++)
                for (var k = 0; k < TargetCount; k++)
                    _bank[h][k].LoadState(bank.Learners[idx++]);
            _scaler.LoadState(artifact.Scaler);
        }
        catch (ArgumentException)
        {
            Reset();
            return false;
        }

        _ewmaVolume = artifact.Scalar("ewma_volume");
        _ewmaLogVolume = artifact.Scalar("ewma_log_volume");
        _ewmaDeltaRatio = artifact.Scalar("ewma_delta_ratio");
        _ewmaInitialized = artifact.Scalar("ewma_initialized") > 0.5;
        _samplesSeen = artifact.SamplesTrained;
        return true;
    }

    private OnlineLinearRegression[][] CreateBank()
    {
        var bank = new OnlineLinearRegression[_options.MaxHorizon][];
        for (var h = 0; h < _options.MaxHorizon; h++)
        {
            bank[h] = new OnlineLinearRegression[TargetCount];
            for (var k = 0; k < TargetCount; k++)
                bank[h][k] = new OnlineLinearRegression(FeatureDim, _options.Lambda);
        }
        return bank;
    }

    /// <summary>A sealed bar can carry a degenerate footprint (all prints filtered, one-sided
    /// flow); such bars stay in the history for time-keeping but produce no learning signal.</summary>
    private static bool IsUsable(FootprintBarSummary bar) =>
        double.IsFinite(bar.Poc) && bar.Poc > 0 &&
        double.IsFinite(bar.High) && double.IsFinite(bar.Low) &&
        bar.TotalVolume > 0;

    private void LearnRealizedPendings(FootprintBarSummary realized, bool realizedUsable)
    {
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (p.TargetIndex > _barIndex) continue;
            _pending.RemoveAt(i);
            if (p.TargetIndex < _barIndex || !realizedUsable) continue;

            var targets = ComputeTargets(p, realized);
            var learners = _bank[p.Horizon - 1];
            for (var k = 0; k < TargetCount; k++) learners[k].Update(p.Features, targets[k]);

            if (p.Horizon != 1) continue;
            var realizedDeltaTicks = (realized.Poc - p.ReferencePoc) / _tickSize;
            if (p.ScoreMl) _mlMetrics.Score(p.MlPocDeltaTicks, realizedDeltaTicks);
            _baselineMetrics.Score(p.BaselinePocDeltaTicks, realizedDeltaTicks);
        }
    }

    private double[] ComputeTargets(PendingSnapshot p, FootprintBarSummary realized)
    {
        var buyPoc = double.IsFinite(realized.BuyPoc) ? realized.BuyPoc : realized.Poc;
        var sellPoc = double.IsFinite(realized.SellPoc) ? realized.SellPoc : realized.Poc;
        return new[]
        {
            (realized.Poc - p.ReferencePoc) / _tickSize,
            (buyPoc - p.ReferencePoc) / _tickSize,
            (sellPoc - p.ReferencePoc) / _tickSize,
            Math.Log(1.0 + realized.TotalVolume) - p.EwmaLogVolume,
            Math.Clamp(realized.Delta / Math.Max(1.0, p.EwmaVolume), -DeltaClamp, DeltaClamp),
        };
    }

    private FootprintForecastBar Reconstruct(FootprintBarSummary reference, double[] y, int horizon)
    {
        var poc = reference.Poc + y[0] * _tickSize;
        var buyPoc = reference.Poc + y[1] * _tickSize;
        var sellPoc = reference.Poc + y[2] * _tickSize;
        var logVolume = Math.Clamp(_ewmaLogVolume + y[3], 0.0, 30.0);
        var volume = Math.Max(0.0, Math.Exp(logVolume) - 1.0);
        var delta = Math.Clamp(y[4], -DeltaClamp, DeltaClamp) * Math.Max(1.0, _ewmaVolume);
        return new FootprintForecastBar(horizon, poc, buyPoc, sellPoc, volume, delta);
    }

    private void UpdateEwmas(FootprintBarSummary bar)
    {
        var volume = (double)bar.TotalVolume;
        var logVolume = Math.Log(1.0 + volume);
        var deltaRatio = bar.Delta / Math.Max(1.0, volume);
        if (!_ewmaInitialized)
        {
            _ewmaVolume = volume;
            _ewmaLogVolume = logVolume;
            _ewmaDeltaRatio = deltaRatio;
            _ewmaInitialized = true;
            return;
        }
        _ewmaVolume += _ewmaAlpha * (volume - _ewmaVolume);
        _ewmaLogVolume += _ewmaAlpha * (logVolume - _ewmaLogVolume);
        _ewmaDeltaRatio += _ewmaAlpha * (deltaRatio - _ewmaDeltaRatio);
    }

    /// <summary>Builds the raw (pre-standardization) feature vector from the last
    /// <see cref="LagBarsRequired"/> bars. False when the lag window is short or holds a
    /// degenerate bar — no forecast is made for that seal.</summary>
    private bool TryBuildRawFeatures(double baselineNextPoc, out double[] raw)
    {
        raw = Array.Empty<double>();
        var n = _history.Count;
        if (n < LagBarsRequired) return false;
        for (var i = 1; i <= LagBarsRequired; i++)
            if (!IsUsable(_history[n - i])) return false;

        var bar = _history[n - 1];
        var prior1 = _history[n - 2];
        var prior2 = _history[n - 3];
        var prior3 = _history[n - 4];

        var buyPoc = double.IsFinite(bar.BuyPoc) ? bar.BuyPoc : bar.Poc;
        var sellPoc = double.IsFinite(bar.SellPoc) ? bar.SellPoc : bar.Poc;
        var range = bar.High - bar.Low;
        var pocPosition = range > 0 ? (bar.Poc - bar.Low) / range : 0.5;
        var valueAreaWidth = double.IsFinite(bar.ValueAreaHigh) && double.IsFinite(bar.ValueAreaLow)
            ? (bar.ValueAreaHigh - bar.ValueAreaLow) / _tickSize
            : 0.0;
        var deltaRatio = bar.Delta / Math.Max(1.0, (double)bar.TotalVolume);
        var baselineValid = double.IsFinite(baselineNextPoc);

        raw = new double[FeatureDim]
        {
            1.0,
            (bar.Poc - prior1.Poc) / _tickSize,
            (prior1.Poc - prior2.Poc) / _tickSize,
            (prior2.Poc - prior3.Poc) / _tickSize,
            (buyPoc - sellPoc) / _tickSize,
            pocPosition,
            valueAreaWidth,
            range / _tickSize,
            deltaRatio,
            _ewmaDeltaRatio,
            Math.Log(1.0 + bar.TotalVolume) - _ewmaLogVolume,
            (bar.CumulativeDelta - prior3.CumulativeDelta) / Math.Max(1.0, _ewmaVolume),
            bar.StackedBuy - bar.StackedSell,
            bar.QualityMultiplier,
            baselineValid ? (baselineNextPoc - bar.Poc) / _tickSize : 0.0,
            baselineValid ? 1.0 : 0.0,
        };
        return true;
    }

    /// <summary>One outstanding prediction awaiting its realized target bar. The scaled feature
    /// vector is retained so the learner updates against exactly what it predicted from; the EWMA
    /// snapshots pin the volume/delta target definitions to the reference bar's state.</summary>
    private sealed record PendingSnapshot(
        long TargetIndex,
        int Horizon,
        double[] Features,
        double ReferencePoc,
        double EwmaLogVolume,
        double EwmaVolume,
        double MlPocDeltaTicks,
        double BaselinePocDeltaTicks,
        bool ScoreMl);
}
