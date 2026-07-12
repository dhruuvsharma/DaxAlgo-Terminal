# index/Core — per-file index (Windows tree)

Generated 2026-07-12. Grep by filename/keyword. LOC > 400 => never read whole; rg then ranged reads.
Editions: B=Basic, I=Intermediate, P=Pro (private repo consumes this tree); dev=test-only.

| File | LOC | Tree | Project | Ed | Pub | Purpose |
|---|---|---|---|---|---|---|
| `src/windows/Core/TradingTerminal.Core/MarketData/TradableInstrument.cs` | 22 | win | TradingTerminal.Core | B I P | Y | A broker-neutral, user-facing tradable instrument: a display label, a grouping |
| `src/windows/Core/TradingTerminal.Core/MarketData/VolumeTimeBucketer.cs` | 125 | win | TradingTerminal.Core | B I P | Y | Total volume in the bucket (≈ the target size B, modulo the |
| `src/windows/Core/TradingTerminal.Core/Ml/DepthStepSampler.cs` | 67 | win | TradingTerminal.Core | B I P | Y | The last step boundary a summary was emitted for — the caller's |
| `src/windows/Core/TradingTerminal.Core/Ml/EwmaForecaster.cs` | 67 | win | TradingTerminal.Core | B I P | Y | Algorithm discriminator stored in |
| `src/windows/Core/TradingTerminal.Core/Ml/FactorComputation.cs` | 193 | win | TradingTerminal.Core | B I P | Y | One aggregated bar over |
| `src/windows/Core/TradingTerminal.Core/Ml/FootprintNextBarPredictor.cs` | 408 | win | TradingTerminal.Core | B I P | Y | Model-family discriminator this predictor's artifacts are filed under in the registry. |
| `src/windows/Core/TradingTerminal.Core/Ml/FootprintPredictionModels.cs` | 89 | win | TradingTerminal.Core | B I P | Y | Projects a sealed Core bar (plus the render-layer argmax POCs and value |
| `src/windows/Core/TradingTerminal.Core/Ml/Forecasters.cs` | 88 | win | TradingTerminal.Core | B I P | Y | Recursive least squares with exponential forgetting — adaptive, second-order, |
| `src/windows/Core/TradingTerminal.Core/Ml/IModelRegistry.cs` | 58 | win | TradingTerminal.Core | B I P | Y | What the registry hands back after a successful |
| `src/windows/Core/TradingTerminal.Core/Ml/IOnlineForecaster.cs` | 60 | win | TradingTerminal.Core | B I P | Y | Discriminator identifying the algorithm — matches |
| `src/windows/Core/TradingTerminal.Core/Ml/ModelArtifact.cs` | 139 | win | TradingTerminal.Core | B I P | Y | Current on-disk schema version. Increment on any breaking shape change. |
| `src/windows/Core/TradingTerminal.Core/Ml/ModelArtifactJson.cs` | 22 | win | TradingTerminal.Core | B I P | Y | Canonical System.Text.Json settings for serializing (and its option |
| `src/windows/Core/TradingTerminal.Core/Ml/OnlineFeatureScaler.cs` | 116 | win | TradingTerminal.Core | B I P | Y | Folds one raw feature vector into the running mean/variance estimates. |
| `src/windows/Core/TradingTerminal.Core/Ml/OnlineGradientDescent.cs` | 70 | win | TradingTerminal.Core | B I P | Y | Algorithm discriminator stored in |
| `src/windows/Core/TradingTerminal.Core/Ml/OnlineLinearRegression.cs` | 120 | win | TradingTerminal.Core | B I P | Y | Algorithm discriminator stored in |
| `src/windows/Core/TradingTerminal.Core/Ml/OnlineLogisticRegression.cs` | 73 | win | TradingTerminal.Core | B I P | Y | Algorithm discriminator stored in |
| `src/windows/Core/TradingTerminal.Core/Ml/OrderBookEventLabeler.cs` | 30 | win | TradingTerminal.Core | B I P | Y | Spread widened: the max spread observed over the window reached at least |
| `src/windows/Core/TradingTerminal.Core/Ml/OrderBookMicroPredictor.cs` | 527 | win | TradingTerminal.Core | B I P | Y | Model-family discriminator this predictor's artifacts are filed under in the registry. |
| `src/windows/Core/TradingTerminal.Core/Ml/OrderBookPredictionModels.cs` | 178 | win | TradingTerminal.Core | B I P | Y | Projects one depth snapshot (plus the accumulated flow) into the step shape, |
| `src/windows/Core/TradingTerminal.Core/Ml/RollingBrierScore.cs` | 60 | win | TradingTerminal.Core | B I P | Y | Scores one realized event forecast. Non-finite probabilities are ignored; |
| `src/windows/Core/TradingTerminal.Core/Ml/RollingForecastMetrics.cs` | 63 | win | TradingTerminal.Core | B I P | Y | Scores one realized forecast, both in ticks relative to the reference bar's |
| `src/windows/Core/TradingTerminal.Core/Ml/TripleBarrierLabeler.cs` | 79 | win | TradingTerminal.Core | B I P | Y | López de Prado (2018) "Advances in Financial Machine Learning" — triple-barrier |
| `src/windows/Core/TradingTerminal.Core/Notifications/INotificationEnricher.cs` | 20 | win | TradingTerminal.Core | B I P | Y | Whether this enricher should run for this notification. |
| `src/windows/Core/TradingTerminal.Core/Notifications/INotificationPublisher.cs` | 14 | win | TradingTerminal.Core | B I P | Y | What strategies call to surface a signal/trade. Implementations buffer and dispatch |
| `src/windows/Core/TradingTerminal.Core/Notifications/INotificationTransport.cs` | 17 | win | TradingTerminal.Core | B I P | Y | Stable name for diagnostics ("telegram", "discord"). |
| `src/windows/Core/TradingTerminal.Core/Notifications/ISignalGate.cs` | 15 | win | TradingTerminal.Core | B I P | Y | True if this notification should be dropped before reaching the transports. |
| `src/windows/Core/TradingTerminal.Core/Notifications/NotificationKind.cs` | 25 | win | TradingTerminal.Core | B I P | Y | An armed-and-fired signal — the strategy would (or did) act on it. |
| `src/windows/Core/TradingTerminal.Core/Notifications/StrategyNotification.cs` | 24 | win | TradingTerminal.Core | B I P | Y | One unit of "something a strategy wants the user to know about." |
| `src/windows/Core/TradingTerminal.Core/Quant/CurveFitting.cs` | 276 | win | TradingTerminal.Core | B I P | Y | Curve families available to chart overlay fits (e.g. the footprint POC regressions). |
| `src/windows/Core/TradingTerminal.Core/Quant/DeflatedSharpe.cs` | 128 | win | TradingTerminal.Core | B I P | Y | Standard-normal CDF Φ(x) via the complementary error function. |
| `src/windows/Core/TradingTerminal.Core/Quant/EwRegression.cs` | 186 | win | TradingTerminal.Core | B I P | Y | Fitted value ŷ = α + β·x at an arbitrary x. |
| `src/windows/Core/TradingTerminal.Core/Quant/FirstPassage.cs` | 80 | win | TradingTerminal.Core | B I P | Y | The pure continuous-path first-passage probability (no gap adjustment). |
| `src/windows/Core/TradingTerminal.Core/Quant/HawkesProcess.cs` | 109 | win | TradingTerminal.Core | B I P | Y | Current decayed excitation sum S at the last event time (diagnostic). |
| `src/windows/Core/TradingTerminal.Core/Quant/InformationCoefficient.cs` | 104 | win | TradingTerminal.Core | B I P | Y | Fractional ranks with tie-averaging. |
| `src/windows/Core/TradingTerminal.Core/Quant/IsotonicCalibration.cs` | 139 | win | TradingTerminal.Core | B I P | Y | g(C): the calibrated expected forward return at composite C (interpolated). |
| `src/windows/Core/TradingTerminal.Core/Quant/KalmanPocPredictor.cs` | 151 | win | TradingTerminal.Core | B I P | Y | True once at least one observation has initialised the state. |
| `src/windows/Core/TradingTerminal.Core/Quant/KyleResidual.cs` | 154 | win | TradingTerminal.Core | B I P | Y | Result of a rolling Kyle-lambda regression r = λ·Δ + ε over |
| `src/windows/Core/TradingTerminal.Core/Quant/LedoitWolf.cs` | 197 | win | TradingTerminal.Core | B I P | Y | Converts a covariance matrix to a correlation matrix (unit diagonal; guards σ→0). |
| `src/windows/Core/TradingTerminal.Core/Quant/NeweyWest.cs` | 83 | win | TradingTerminal.Core | B I P | Y | Default automatic bandwidth L = floor(4·(n/100)^(2/9)), at least 0. |
| `src/windows/Core/TradingTerminal.Core/Quant/SignalWeights.cs` | 57 | win | TradingTerminal.Core | B I P | Y | Combination-weight solver for blending k signals into one composite. The mean-variance optimal |
| `src/windows/Core/TradingTerminal.Core/Quant/Surfaces/LiveBarSeries.cs` | 112 | win | TradingTerminal.Core | B I P | Y | Committed bars + the forming bar (if any). |
| `src/windows/Core/TradingTerminal.Core/Quant/Surfaces/SurfaceAxes.cs` | 132 | win | TradingTerminal.Core | B I P | Y | Top-level surface mode — decides what the X/Y axes can be bound |
| `src/windows/Core/TradingTerminal.Core/Quant/Surfaces/SurfaceFormulaParser.cs` | 260 | win | TradingTerminal.Core | B I P | Y | Distinct identifiers referenced by the formula (lower-cased). |
| `src/windows/Core/TradingTerminal.Core/Quant/Surfaces/SurfaceGridBuilder.cs` | 486 | win | TradingTerminal.Core | B I P | Y | One configured axis: the picked option id, the bin range (ignored when |
| `src/windows/Core/TradingTerminal.Core/Quant/Surfaces/SurfaceMetrics.cs` | 245 | win | TradingTerminal.Core | B I P | Y | How a surface axis value should be rendered (tick labels, tooltips, stats). |
| `src/windows/Core/TradingTerminal.Core/Quant/TimeSeries/ArimaModel.cs` | 233 | win | TradingTerminal.Core | B I P | Y | A fitted ARIMA(p,d,q) model over a (log-)price series. |
| `src/windows/Core/TradingTerminal.Core/Quant/TimeSeries/GarchModel.cs` | 102 | win | TradingTerminal.Core | B I P | Y | Fits GARCH(1,1) to a return series (fractional returns, not %). Null when |
| `src/windows/Core/TradingTerminal.Core/Quant/TimeSeries/KalmanFilters.cs` | 235 | win | TradingTerminal.Core | B I P | Y | Local level model. |
| `src/windows/Core/TradingTerminal.Core/Quant/TimeSeries/NelderMead.cs` | 90 | win | TradingTerminal.Core | B I P | Y | x = centroid + coeff·(centroid − worst); coeff −0.5 gives the inside |
| `src/windows/Core/TradingTerminal.Core/Quant/TimeSeries/Ols.cs` | 125 | win | TradingTerminal.Core | B I P | Y | Gauss-Jordan inverse with partial pivoting; null when singular. |
| `src/windows/Core/TradingTerminal.Core/Quant/TimeSeries/SeriesTransforms.cs` | 149 | win | TradingTerminal.Core | B I P | Y | Stationarity-inducing transform applied to a price series before testing/modelling. |
| `src/windows/Core/TradingTerminal.Core/Quant/TimeSeries/StationarityTests.cs` | 195 | win | TradingTerminal.Core | B I P | Y | KPSS level-stationarity critical values (Kwiatkowski et al. 1992, Table 1, η_μ). |
| `src/windows/Core/TradingTerminal.Core/QuantConnect/ILeanClient.cs` | 33 | win | TradingTerminal.Core | B I P | Y | Which backend this instance drives. |
| `src/windows/Core/TradingTerminal.Core/QuantConnect/LeanModels.cs` | 68 | win | TradingTerminal.Core | B I P | Y | How LEAN backtests are executed. The seam is engine-agnostic so the cloud |
| `src/windows/Core/TradingTerminal.Core/QuantConnect/LeanOptions.cs` | 35 | win | TradingTerminal.Core | B I P | Y | Local CLI now; Cloud is reserved for the future REST client. |
| `src/windows/Core/TradingTerminal.Core/Regime/IMarketRegimeProvider.cs` | 22 | win | TradingTerminal.Core | B I P | Y | The most recent snapshot, or |
| `src/windows/Core/TradingTerminal.Core/Regime/Instrument/IInstrumentRegimeProvider.cs` | 25 | win | TradingTerminal.Core | B I P | Y | Pull recent bars + optional depth, compute and return a snapshot. Folds |
| `src/windows/Core/TradingTerminal.Core/Regime/Instrument/InstrumentRegimeBand.cs` | 38 | win | TradingTerminal.Core | B I P | Y | Maps a signed composite score in |
| `src/windows/Core/TradingTerminal.Core/Regime/Instrument/InstrumentRegimeCalculator.cs` | 291 | win | TradingTerminal.Core | B I P | Y | Percentile rank of the current ATR within the trailing |
| `src/windows/Core/TradingTerminal.Core/Regime/Instrument/InstrumentRegimeInputs.cs` | 20 | win | TradingTerminal.Core | B I P | Y | Inputs to . Bars are required; |
| `src/windows/Core/TradingTerminal.Core/Regime/Instrument/InstrumentRegimeSignal.cs` | 33 | win | TradingTerminal.Core | B I P | Y | Close vs N-period SMA — bullish above, bearish below. |
| `src/windows/Core/TradingTerminal.Core/Regime/Instrument/InstrumentRegimeSnapshot.cs` | 53 | win | TradingTerminal.Core | B I P | Y | One per-instrument regime computation: the signed composite, its band, the breakdown of |
| `src/windows/Core/TradingTerminal.Core/Regime/Instrument/InstrumentSignalScore.cs` | 16 | win | TradingTerminal.Core | B I P | Y | One scored sub-signal of the per-instrument composite. is normalised to |
| `src/windows/Core/TradingTerminal.Core/Regime/MarketRegimeCalculator.cs` | 320 | win | TradingTerminal.Core | B I P | Y | Composite weights — sum to 1.0. Matches the upstream WEIGHTS table. |
| `src/windows/Core/TradingTerminal.Core/Regime/MarketRegimeSnapshot.cs` | 48 | win | TradingTerminal.Core | B I P | Y | Sentinel for "we have no data yet" — shown before the first |
| `src/windows/Core/TradingTerminal.Core/Regime/RegimeCategory.cs` | 39 | win | TradingTerminal.Core | B I P | Y | Survey + index sentiment (CNN Fear &amp; Greed, AAII bull/bear). |
| `src/windows/Core/TradingTerminal.Core/Regime/RegimeCategoryScore.cs` | 16 | win | TradingTerminal.Core | B I P | Y | One scored sub-signal of the composite. is the points this |
| `src/windows/Core/TradingTerminal.Core/Regime/RegimeInputs.cs` | 53 | win | TradingTerminal.Core | B I P | Y | Sector ETF close series keyed by symbol (XLK, XLF, …) for the |
| `src/windows/Core/TradingTerminal.Core/Regime/RegimeState.cs` | 42 | win | TradingTerminal.Core | B I P | Y | Maps a 0–100 composite score to its band. |
| `src/windows/Core/TradingTerminal.Core/Research/EnvHash.cs` | 17 | win | TradingTerminal.Core | B I P | Y | An unresolved/unknown environment (e.g. a reproduction that never built). |
| `src/windows/Core/TradingTerminal.Core/Research/EnvResolutionPlan.cs` | 28 | win | TradingTerminal.Core | B I P | Y | An empty/unresolved plan carrying just an |
| `src/windows/Core/TradingTerminal.Core/Research/IEnvResolverClient.cs` | 24 | win | TradingTerminal.Core | B I P | Y | True when this client expects the sidecar to be reachable (Http when |
| `src/windows/Core/TradingTerminal.Core/Research/IPaperIngestClient.cs` | 37 | win | TradingTerminal.Core | B I P | Y | An empty/failed result carrying the reason. |
| `src/windows/Core/TradingTerminal.Core/Research/IReplicationConfidenceScorer.cs` | 15 | win | TradingTerminal.Core | B I P | Y | Score a reproduction. A failed result or empty manifest scores low (never |
| `src/windows/Core/TradingTerminal.Core/Research/IReproJobStore.cs` | 33 | win | TradingTerminal.Core | B I P | Y | Insert or update a job (keyed by |
| `src/windows/Core/TradingTerminal.Core/Research/IReproOrchestrator.cs` | 27 | win | TradingTerminal.Core | B I P | Y | Submit a spec. Returns the cached succeeded job if one exists for |
| `src/windows/Core/TradingTerminal.Core/Research/IReproSignalBridge.cs` | 19 | win | TradingTerminal.Core | B I P | Y | Map a succeeded result's declared artifact to a time-sorted, InstrumentId-keyed |
| `src/windows/Core/TradingTerminal.Core/Research/ISandboxRunner.cs` | 42 | win | TradingTerminal.Core | B I P | Y | Which backend this runner implements. |
| `src/windows/Core/TradingTerminal.Core/Research/MinimalReproPlan.cs` | 24 | win | TradingTerminal.Core | B I P | Y | An empty/failed plan carrying the reason. |
| `src/windows/Core/TradingTerminal.Core/Research/PaperRef.cs` | 9 | win | TradingTerminal.Core | B I P | Y | A resolved research paper: its arXiv id, title, and canonical URL. The |
| `src/windows/Core/TradingTerminal.Core/Research/ReplicationConfidence.cs` | 15 | win | TradingTerminal.Core | B I P | Y | No confidence (e.g. a failed or not-yet-scored reproduction). |
| `src/windows/Core/TradingTerminal.Core/Research/ReplicationCostEstimate.cs` | 14 | win | TradingTerminal.Core | B I P | Y | A zero/unknown estimate (e.g. before any minimal run has completed). |
| `src/windows/Core/TradingTerminal.Core/Research/RepoRef.cs` | 9 | win | TradingTerminal.Core | B I P | Y | A candidate code repository for a paper, pinned to an exact . |
| `src/windows/Core/TradingTerminal.Core/Research/ReproArtifact.cs` | 13 | win | TradingTerminal.Core | B I P | Y | A single output file produced by a reproduction, identified by its declared |
| `src/windows/Core/TradingTerminal.Core/Research/ReproJob.cs` | 45 | win | TradingTerminal.Core | B I P | Y | True when the job has reached a terminal state and will not |
| `src/windows/Core/TradingTerminal.Core/Research/ReproResult.cs` | 31 | win | TradingTerminal.Core | B I P | Y | A failed result carrying the reason and (where known) the provenance triple. |
| `src/windows/Core/TradingTerminal.Core/Research/ReproSignalKind.cs` | 24 | win | TradingTerminal.Core | B I P | Y | A continuous score/prediction (e.g. a forecasted return or alpha). Carried as |
| `src/windows/Core/TradingTerminal.Core/Research/ReproSignalManifest.cs` | 59 | win | TradingTerminal.Core | B I P | Y | An empty manifest carrying whatever provenance is known — the never-throw failure |
| `src/windows/Core/TradingTerminal.Core/Research/ReproSpec.cs` | 44 | win | TradingTerminal.Core | B I P | Y | Convenience for a spec with no extra config. |
| `src/windows/Core/TradingTerminal.Core/Research/ReproStatus.cs` | 36 | win | TradingTerminal.Core | B I P | Y | Accepted and waiting for a sandbox slot. |
| `src/windows/Core/TradingTerminal.Core/Research/ReproducedSignal.cs` | 22 | win | TradingTerminal.Core | B I P | Y | One timestamped output of a reproduction, mapped onto canonical identity: an |
| `src/windows/Core/TradingTerminal.Core/Research/SandboxKind.cs` | 18 | win | TradingTerminal.Core | B I P | Y | A disposable Docker container (deny-by-default isolation). |
| `src/windows/Core/TradingTerminal.Core/Research/SandboxPolicy.cs` | 35 | win | TradingTerminal.Core | B I P | Y | True when no egress host is allowed — the runner must pass |
| `src/windows/Core/TradingTerminal.Core/Research/SandboxQuota.cs` | 19 | win | TradingTerminal.Core | B I P | Y | The strict default: 1 CPU, 1 GiB RAM, 256 pids, 1 GiB |
| `src/windows/Core/TradingTerminal.Core/Risk/IRiskManager.cs` | 27 | win | TradingTerminal.Core | B I P | Y | Pre-trade risk check. Sits between the strategy and the broker / simulated |
| `src/windows/Core/TradingTerminal.Core/Risk/RiskManager.cs` | 117 | win | TradingTerminal.Core | B I P | Y | Current net signed position per symbol — exposed for telemetry / tests. |
| `src/windows/Core/TradingTerminal.Core/Risk/RiskOptions.cs` | 24 | win | TradingTerminal.Core | B I P | Y | Maximum absolute net position per symbol, in contracts/shares. 0 = disabled. |
| `src/windows/Core/TradingTerminal.Core/Session/SessionContext.cs` | 32 | win | TradingTerminal.Core | B I P | Y | Mutable singleton populated by the login flow once the user is authenticated. |
| `src/windows/Core/TradingTerminal.Core/Strategies/Authoring/IStrategyCodegenClient.cs` | 59 | win | TradingTerminal.Core | B I P | Y | Who is speaking in a codegen conversation. |
| `src/windows/Core/TradingTerminal.Core/Strategies/Authoring/IStrategyCompiler.cs` | 21 | win | TradingTerminal.Core | B I P | Y | Compiles a user-authored into a runnable |
| `src/windows/Core/TradingTerminal.Core/Strategies/Authoring/StrategyCompileResult.cs` | 26 | win | TradingTerminal.Core | B I P | Y | Outcome of compiling a . On success, |
| `src/windows/Core/TradingTerminal.Core/Strategies/Authoring/StrategyDiagnostic.cs` | 25 | win | TradingTerminal.Core | B I P | Y | Severity of a |
| `src/windows/Core/TradingTerminal.Core/Strategies/Authoring/StrategyScript.cs` | 16 | win | TradingTerminal.Core | B I P | Y | A user-authored strategy awaiting compilation: a stable id, a friendly display name, |
| `src/windows/Core/TradingTerminal.Core/Strategies/IStrategyFactory.cs` | 16 | win | TradingTerminal.Core | B I P | Y | Resolves a registered strategy into a (view, view-model) pair. The shell never |
| `src/windows/Core/TradingTerminal.Core/Strategies/ITradingStrategy.cs` | 72 | win | TradingTerminal.Core | B I P | Y | Stable, unique identifier (e.g. "example.nvda.3m"). Used to dedupe tabs. |
| `src/windows/Core/TradingTerminal.Core/Strategies/Parameters/ParameterKind.cs` | 25 | win | TradingTerminal.Core | B I P | Y | Whole number. Backed by |
| `src/windows/Core/TradingTerminal.Core/Strategies/Parameters/StrategyParameter.cs` | 100 | win | TradingTerminal.Core | B I P | Y | Stable machine key, used to read the value back. Unique within a |
| `src/windows/Core/TradingTerminal.Core/Strategies/Parameters/StrategyParameterSchema.cs` | 45 | win | TradingTerminal.Core | B I P | Y | A schema with no tunables — the default for strategies that take |
| `src/windows/Core/TradingTerminal.Core/Strategies/Parameters/StrategyParameters.cs` | 162 | win | TradingTerminal.Core | B I P | Y | Sets a value, coercing and clamping it against the parameter declaration. |
| `src/windows/Core/TradingTerminal.Core/Strategies/StrategyAssetScope.cs` | 22 | win | TradingTerminal.Core | B I P | Y | Whether a strategy operates on a single instrument at a time or |
| `src/windows/Core/TradingTerminal.Core/Strategies/StrategyBrokerCapability.cs` | 54 | win | TradingTerminal.Core | B I P | Y | The broker capability matrix that backs 's default: |
| `src/windows/Core/TradingTerminal.Core/Strategies/StrategyDataRequirement.cs` | 38 | win | TradingTerminal.Core | B I P | Y | No declared requirement. |
| `src/windows/Core/TradingTerminal.Core/Strategies/StrategyFactoryRegistration.cs` | 11 | win | TradingTerminal.Core | B I P | Y | Pure-data record describing how to build the (view, view-model) pair for a |
| `src/windows/Core/TradingTerminal.Core/Strategies/StrategyHost.cs` | 12 | win | TradingTerminal.Core | B I P | Y | A concrete (view, view-model) pair plus metadata. The view and view-model are |
| `src/windows/Core/TradingTerminal.Core/Time/IClock.cs` | 13 | win | TradingTerminal.Core | B I P | Y | Wall-clock abstraction. Real code uses SystemClock (); |
| `src/windows/Core/TradingTerminal.Core/Trading/IFeeModel.cs` | 70 | win | TradingTerminal.Core | B I P | Y | Whether a fill took (crossed the spread) or made (rested) liquidity. |
| `src/windows/Core/TradingTerminal.Core/Trading/IOrderRouter.cs` | 22 | win | TradingTerminal.Core | B I P | Y | Cancels a working order by its client-assigned id. Idempotent. |
| `src/windows/Core/TradingTerminal.Core/Trading/OrderEvent.cs` | 19 | win | TradingTerminal.Core | B I P | Y | A single transition in the lifecycle of an order. and |
| `src/windows/Core/TradingTerminal.Core/Trading/OrderRequest.cs` | 20 | win | TradingTerminal.Core | B I P | Y | A request to submit a single order. is a caller-generated |
| `src/windows/Core/TradingTerminal.Core/Trading/OrderResult.cs` | 12 | win | TradingTerminal.Core | B I P | Y | Synchronous return value from PlaceOrderAsync. Reflects the order's state at |
| `src/windows/Core/TradingTerminal.Core/Trading/OrderSide.cs` | 7 | win | TradingTerminal.Core | B I P | Y |  |
| `src/windows/Core/TradingTerminal.Core/Trading/OrderState.cs` | 17 | win | TradingTerminal.Core | B I P | Y | Lifecycle of a single order. is the optimistic local state |
| `src/windows/Core/TradingTerminal.Core/Trading/OrderType.cs` | 9 | win | TradingTerminal.Core | B I P | Y |  |
| `src/windows/Core/TradingTerminal.Core/Trading/TimeInForce.cs` | 9 | win | TradingTerminal.Core | B I P | Y |  |
