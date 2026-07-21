# index/Tests — per-file index (Windows tree)

Generated from the current source tree. Grep by filename/keyword. LOC > 400 => never read whole; rg then ranged reads.
Editions: B=Basic, I=Intermediate, P=Pro (private repo consumes this tree); dev=test-only.

| File | LOC | Tree | Project | Ed | Pub | Purpose |
|---|---|---|---|---|---|---|
| `tests/TradingTerminal.Tests/AiAnalyst/AiAnalystEnricherTests.cs` | 146 | win | TradingTerminal.Tests | dev | Y |  |
| `tests/TradingTerminal.Tests/AiAnalyst/HttpAiAnalystClientTests.cs` | 140 | win | TradingTerminal.Tests | dev | Y |  |
| `tests/TradingTerminal.Tests/AiAnalyst/NullAiAnalystClientTests.cs` | 37 | win | TradingTerminal.Tests | dev | Y |  |
| `tests/TradingTerminal.Tests/AssemblyInfo.cs` | 7 | win | TradingTerminal.Tests | dev | N |  |
| `tests/TradingTerminal.Tests/Authoring/VibeQuantTranscriptTests.cs` | 137 | win | TradingTerminal.Tests | dev | Y | The pure logic under the Vibe Quant agent workspace (issue #29): the |
| `tests/TradingTerminal.Tests/Backtest/DuckDbParquetQueryServiceTests.cs` | 98 | win | TradingTerminal.Tests | dev | Y | Exercises against real Parquet files produced by |
| `tests/TradingTerminal.Tests/Controls/ChartPanelTests.cs` | 103 | win | TradingTerminal.Tests | dev | Y | The three chart tools are now embeddable UserControls (an authored strategy composes |
| `tests/TradingTerminal.Tests/Controls/ChartsViewModelLifetimeTests.cs` | 98 | win | TradingTerminal.Tests | dev | Y |  |
| `tests/TradingTerminal.Tests/Controls/ComposedStrategyViewTests.cs` | 197 | win | TradingTerminal.Tests | dev | Y | Panel view-models resolve their pipeline seams from here; with no instrument pinned |
| `tests/TradingTerminal.Tests/Controls/InstrumentPickerFilterTests.cs` | 190 | win | TradingTerminal.Tests | dev | Y | Unit tests for — the shared logic behind every instrument |
| `tests/TradingTerminal.Tests/Controls/InstrumentPickerTests.cs` | 57 | win | TradingTerminal.Tests | dev | Y | Regression test for the strategy-window crash "Cannot find resource named |
| `tests/TradingTerminal.Tests/Controls/LiveSignalStrategyViewModelLifetimeTests.cs` | 114 | win | TradingTerminal.Tests | dev | Y |  |
| `tests/TradingTerminal.Tests/Controls/RecorderPanelViewTests.cs` | 57 | win | TradingTerminal.Tests | dev | Y | The L3 chip is a deliberate, permanently-dim placeholder: no broker in this |
| `tests/TradingTerminal.Tests/Controls/WpfTestApp.cs` | 84 | win | TradingTerminal.Tests | dev | N | Any style the panels resolve by StaticResource — its presence means the |
| `tests/TradingTerminal.Tests/Login/LoginFormEditionCompositionTests.cs` | 98 | win | TradingTerminal.Tests | dev | Y | The broker-neutral services every edition provides before AddLogin. |
| `tests/TradingTerminal.Tests/Login/ServiceDependencyViewModelTests.cs` | 61 | win | TradingTerminal.Tests | dev | Y |  |
| `tests/TradingTerminal.Tests/MarketData/LocalParquetLakeExporterTests.cs` | 132 | win | TradingTerminal.Tests | dev | Y | Minimal hand-rolled |
| `tests/TradingTerminal.Tests/Strategies/StrategyFactoryTests.cs` | 65 | win | TradingTerminal.Tests | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Analytics/CorrelationCalculatorTests.cs` | 103 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Backtest/BacktestSessionTests.cs` | 106 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Backtest/BacktestStoreSourceTests.cs` | 197 | win | TradingTerminal.Tests.Headless | dev | Y | Minimal hand-rolled |
| `tests/TradingTerminal.Tests.Headless/Backtest/FeeModelTests.cs` | 88 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Backtest/MonteCarloTests.cs` | 56 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Backtest/ParquetTickRoundtripTests.cs` | 65 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Backtest/ParquetTradeTapeTests.cs` | 79 | win | TradingTerminal.Tests.Headless | dev | Y | Covers the optional backtest trade tape: trades round-trip through parquet with their |
| `tests/TradingTerminal.Tests.Headless/Backtest/StatisticsCalculatorTests.cs` | 88 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Backtest/TransactionCostAnalysisTests.cs` | 86 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Backtesting/BacktestEngineLifecycleTests.cs` | 216 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Backtesting/BacktestEngineTests.cs` | 78 | win | TradingTerminal.Tests.Headless | dev | Y | End-to-end smoke + accounting checks for the new (P1). Drives an |
| `tests/TradingTerminal.Tests.Headless/Backtesting/EngineParityTests.cs` | 127 | win | TradingTerminal.Tests.Headless | dev | Y | Parity gate: the new must reproduce the old |
| `tests/TradingTerminal.Tests.Headless/Backtesting/GeneticAndWalkForwardTests.cs` | 71 | win | TradingTerminal.Tests.Headless | dev | Y | Covers the genetic optimizer and walk-forward analysis built on the new engine. |
| `tests/TradingTerminal.Tests.Headless/Backtesting/GpuFallbackTests.cs` | 62 | win | TradingTerminal.Tests.Headless | dev | Y | Covers the C# side of the GPU accelerator: the support gate and |
| `tests/TradingTerminal.Tests.Headless/Backtesting/GpuParityTests.cs` | 67 | win | TradingTerminal.Tests.Headless | dev | Y | Validates the CUDA accelerator against the CPU optimizer: the GPU's net profit |
| `tests/TradingTerminal.Tests.Headless/Backtesting/KernelRegistryTests.cs` | 73 | win | TradingTerminal.Tests.Headless | dev | Y | Covers kernel discovery by id, schema-driven defaults/clamping, and that a registry-built |
| `tests/TradingTerminal.Tests.Headless/Backtesting/LegacyBridgeTests.cs` | 79 | win | TradingTerminal.Tests.Headless | dev | Y | Cutover check: a legacy |
| `tests/TradingTerminal.Tests.Headless/Backtesting/OptimizerTests.cs` | 74 | win | TradingTerminal.Tests.Headless | dev | Y | Covers the grid optimizer: Cartesian expansion, that it evaluates every combination, |
| `tests/TradingTerminal.Tests.Headless/Backtesting/PythonStrategyTests.cs` | 72 | win | TradingTerminal.Tests.Headless | dev | Y | End-to-end check that a Python-authored strategy (daxalgo_bt example) runs on the new |
| `tests/TradingTerminal.Tests.Headless/Backtesting/StoreFeedAndPortfolioTests.cs` | 95 | win | TradingTerminal.Tests.Headless | dev | Y | Exercises the store-backed feed's k-way merge and the engine's multi-instrument (portfolio) |
| `tests/TradingTerminal.Tests.Headless/Backtesting/VisualTimelineTests.cs` | 63 | win | TradingTerminal.Tests.Headless | dev | Y | Verifies the engine captures a visual timeline only when asked, with OHLC |
| `tests/TradingTerminal.Tests.Headless/Backtesting/WalkForwardGridTests.cs` | 66 | win | TradingTerminal.Tests.Headless | dev | Y | Covers the pluggable walk-forward grid seam: grids live on each strategy's |
| `tests/TradingTerminal.Tests.Headless/Backtesting/WorkerCleanupTests.cs` | 155 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Backtesting/WorkerClientTests.cs` | 485 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Backtesting/WorkerProtocolTests.cs` | 230 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Backtesting/WorkerTestData.cs` | 68 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Infrastructure/BinanceParsingTests.cs` | 207 | win | TradingTerminal.Tests.Headless | dev | Y | Offline tests for the Binance public-feed JSON parsers. No network — they |
| `tests/TradingTerminal.Tests.Headless/Infrastructure/ConnectionManagerTests.cs` | 209 | win | TradingTerminal.Tests.Headless | dev | Y | ConnectAsync returns normally but reports Failed every time (never Connected) — |
| `tests/TradingTerminal.Tests.Headless/Infrastructure/IronBeamParsingTests.cs` | 180 | win | TradingTerminal.Tests.Headless | dev | Y | Offline tests for the Ironbeam WebSocket JSON parsers and symbol mapping. No |
| `tests/TradingTerminal.Tests.Headless/Infrastructure/LondonStrategicEdgeParsingTests.cs` | 185 | win | TradingTerminal.Tests.Headless | dev | Y | Offline tests for the London Strategic Edge WebSocket/REST JSON parsers and symbol |
| `tests/TradingTerminal.Tests.Headless/Infrastructure/SimulatedBrokerClientTests.cs` | 138 | win | TradingTerminal.Tests.Headless | dev | Y | Base test store: writes are swallowed and reads return empty unless a |
| `tests/TradingTerminal.Tests.Headless/Infrastructure/UpstoxParsingTests.cs` | 148 | win | TradingTerminal.Tests.Headless | dev | Y | Tests for the Upstox parsing helpers. The protobuf test encodes a FeedResponse |
| `tests/TradingTerminal.Tests.Headless/MarketData/ArchiveDepthRoundTripTests.cs` | 178 | win | TradingTerminal.Tests.Headless | dev | Y | In-memory |
| `tests/TradingTerminal.Tests.Headless/MarketData/FootprintTimeBucketerTests.cs` | 126 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/MarketData/IndicatorsTests.cs` | 70 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/MarketData/InstrumentDiscoveryServiceTests.cs` | 136 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/MarketData/InstrumentRegistryTests.cs` | 72 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/MarketData/MarketDataHubTests.cs` | 52 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/MarketData/MarketDataIngestServiceTests.cs` | 116 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/MarketData/MarketDataRepositoryTests.cs` | 232 | win | TradingTerminal.Tests.Headless | dev | Y | Test selector that returns the single supplied client for whichever broker is |
| `tests/TradingTerminal.Tests.Headless/MarketData/MicrostructureTests.cs` | 126 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/MarketData/NpgsqlMarketDataStoreTests.cs` | 67 | win | TradingTerminal.Tests.Headless | dev | Y | Integration tests against the docker-compose TimescaleDB. They self-skip (return) when the |
| `tests/TradingTerminal.Tests.Headless/MarketData/OrderFlowImbalanceTests.cs` | 82 | win | TradingTerminal.Tests.Headless | dev | Y | Unit tests for the OBI(T) primitives from arXiv:2507.22712 (trade-based imbalance + the |
| `tests/TradingTerminal.Tests.Headless/MarketData/PerBrokerSqliteMarketDataStoreTests.cs` | 179 | win | TradingTerminal.Tests.Headless | dev | Y | Behaviour of the per-broker, per-stream SQLite store: writes route to one file |
| `tests/TradingTerminal.Tests.Headless/MarketData/QuestDbMarketDataStoreTests.cs` | 124 | win | TradingTerminal.Tests.Headless | dev | Y | Poll a read until it returns the expected count or the timeout |
| `tests/TradingTerminal.Tests.Headless/MarketData/SqliteMarketDataStoreTests.cs` | 131 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/MarketData/SqliteModelRegistryTests.cs` | 144 | win | TradingTerminal.Tests.Headless | dev | Y | Round-trip coverage for the SQLite trained-model registry: save/version/load, latest-per-key |
| `tests/TradingTerminal.Tests.Headless/Ml/DepthStepSamplerTests.cs` | 108 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Ml/FootprintNextBarPredictorTests.cs` | 181 | win | TradingTerminal.Tests.Headless | dev | Y | A clean synthetic bar whose POC sits at |
| `tests/TradingTerminal.Tests.Headless/Ml/ForecasterAlgorithmTests.cs` | 134 | win | TradingTerminal.Tests.Headless | dev | Y | Coverage for the selectable online-learner family behind — the |
| `tests/TradingTerminal.Tests.Headless/Ml/ModelSerializationTests.cs` | 215 | win | TradingTerminal.Tests.Headless | dev | Y | Serialization / checkpoint-round-trip coverage for the model-persistence foundation: the online |
| `tests/TradingTerminal.Tests.Headless/Ml/OnlineLinearRegressionTests.cs` | 61 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Ml/OrderBookEventLabelerTests.cs` | 41 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Ml/OrderBookMicroPredictorTests.cs` | 210 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Ml/RollingBrierScoreTests.cs` | 62 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Ml/RollingForecastMetricsTests.cs` | 71 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Ml/TripleBarrierLabelerTests.cs` | 77 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Plugins/AuthenticodeSignatureInspectorTests.cs` | 81 | win | TradingTerminal.Tests.Headless | dev | Y | Embedded-signed (not catalog-signed) Microsoft binaries in the runtime folder. |
| `tests/TradingTerminal.Tests.Headless/Plugins/DaxPluginPackageTests.cs` | 528 | win | TradingTerminal.Tests.Headless | dev | Y | Builds a source plugin folder (garbage main dll + manifest at the |
| `tests/TradingTerminal.Tests.Headless/Plugins/DaxStrategyBundleTests.cs` | 766 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Plugins/ExternalPluginLoadTests.cs` | 65 | win | TradingTerminal.Tests.Headless | dev | Y | Proves the loader's real file/ALC path: against a GENUINE |
| `tests/TradingTerminal.Tests.Headless/Plugins/FeedSignatureVerifierTests.cs` | 107 | win | TradingTerminal.Tests.Headless | dev | Y | The marketplace feed's trust anchor: a real ECDSA P-256 keypair signs a |
| `tests/TradingTerminal.Tests.Headless/Plugins/FeedSignerTests.cs` | 84 | win | TradingTerminal.Tests.Headless | dev | Y | The signer and verifier are a matched pair (issue #25): a key |
| `tests/TradingTerminal.Tests.Headless/Plugins/PluginCatalogInstallerTests.cs` | 146 | win | TradingTerminal.Tests.Headless | dev | Y | Builds a valid .daxplugin (garbage assembly + manifest) and returns its bytes |
| `tests/TradingTerminal.Tests.Headless/Plugins/PluginCatalogTests.cs` | 169 | win | TradingTerminal.Tests.Headless | dev | Y | The catalog projection (issue #25): a verified feed index joined to what's |
| `tests/TradingTerminal.Tests.Headless/Plugins/PluginConsentTests.cs` | 182 | win | TradingTerminal.Tests.Headless | dev | Y | Covers the unsigned-plugin consent path: under Curated, a plugin that is neither |
| `tests/TradingTerminal.Tests.Headless/Plugins/PluginFeedClientTests.cs` | 127 | win | TradingTerminal.Tests.Headless | dev | Y | The feed client: a correctly-signed fetch verifies + caches; a later outage |
| `tests/TradingTerminal.Tests.Headless/Plugins/PluginIntegrityTests.cs` | 254 | win | TradingTerminal.Tests.Headless | dev | Y | Creates |
| `tests/TradingTerminal.Tests.Headless/Plugins/PluginLifecycleTests.cs` | 233 | win | TradingTerminal.Tests.Headless | dev | Y | A plugin folder whose main "assembly" is garbage bytes — enumerable by |
| `tests/TradingTerminal.Tests.Headless/Plugins/PluginLoaderTests.cs` | 93 | win | TradingTerminal.Tests.Headless | dev | Y | Covers the host plugin loader's discovery + version-gating + registration path |
| `tests/TradingTerminal.Tests.Headless/Plugins/PluginPolicyScannerTests.cs` | 223 | win | TradingTerminal.Tests.Headless | dev | Y | The plugin's private dependencies ship in its folder, so they are scanned |
| `tests/TradingTerminal.Tests.Headless/Plugins/PluginRegistrarGuardTests.cs` | 359 | win | TradingTerminal.Tests.Headless | dev | Y | A host collection shaped like the real one: a credential store plus |
| `tests/TradingTerminal.Tests.Headless/Plugins/PluginSecurityTests.cs` | 135 | win | TradingTerminal.Tests.Headless | dev | Y | Covers the curated-marketplace trust gate: the decisions, the |
| `tests/TradingTerminal.Tests.Headless/Plugins/StrategyBundleStoreTests.cs` | 297 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Quant/CurveFittingTests.cs` | 177 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Quant/DeflatedSharpeTests.cs` | 101 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Quant/EwRegressionTests.cs` | 143 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Quant/FirstPassageTests.cs` | 117 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Quant/FootprintFeaturesTests.cs` | 278 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Quant/HawkesProcessTests.cs` | 60 | win | TradingTerminal.Tests.Headless | dev | Y | Covers the Hawkes self-exciting intensity tracker: it returns the baseline with no |
| `tests/TradingTerminal.Tests.Headless/Quant/InformationCoefficientTests.cs` | 114 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Quant/IsotonicCalibrationTests.cs` | 147 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Quant/KalmanPocPredictorTests.cs` | 66 | win | TradingTerminal.Tests.Headless | dev | Y | Covers the constant-velocity Kalman POC predictor: it should lock onto a linear |
| `tests/TradingTerminal.Tests.Headless/Quant/KyleResidualTests.cs` | 111 | win | TradingTerminal.Tests.Headless | dev | Y | Generates AR(1) signed flow Δ_t = φ·Δ_{t−1} + u_t so the lagged |
| `tests/TradingTerminal.Tests.Headless/Quant/LedoitWolfTests.cs` | 204 | win | TradingTerminal.Tests.Headless | dev | Y | Generates n samples from a k-variate normal with the given (lower-triangular) Cholesky |
| `tests/TradingTerminal.Tests.Headless/Quant/NeweyWestTests.cs` | 164 | win | TradingTerminal.Tests.Headless | dev | Y | Naive OLS slope SE for a simple regression: sqrt( Σe²/(n-2) / S_xx |
| `tests/TradingTerminal.Tests.Headless/Quant/QuantTestRandom.cs` | 17 | win | TradingTerminal.Tests.Headless | dev | Y | Standard-normal draw (mean 0, sd 1) via Box-Muller. |
| `tests/TradingTerminal.Tests.Headless/Quant/SignalWeightsTests.cs` | 118 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Quant/SurfaceLabMathTests.cs` | 416 | win | TradingTerminal.Tests.Headless | dev | Y | Statistics registry math for the 3D Surface Lab (Core/Quant/Surfaces). |
| `tests/TradingTerminal.Tests.Headless/Quant/TimeSeriesMathTests.cs` | 303 | win | TradingTerminal.Tests.Headless | dev | Y | Offline tests for the Machine Learning menu's time-series math in Core — |
| `tests/TradingTerminal.Tests.Headless/Quant/VolumeTimeBucketerTests.cs` | 157 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Regime/AdvancedRegimeCalculatorTests.cs` | 286 | win | TradingTerminal.Tests.Headless | dev | Y | Strictly rising up-candles, 1m apart, all within one UTC session day. |
| `tests/TradingTerminal.Tests.Headless/Regime/BarTimeframeAggregatorTests.cs` | 87 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Regime/MarketRegimeCalculatorTests.cs` | 116 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Research/DockerSandboxRunnerTests.cs` | 82 | win | TradingTerminal.Tests.Headless | dev | Y | Isolation/quota integration tests for the Docker sandbox runner. They SELF-SKIP (return) when |
| `tests/TradingTerminal.Tests.Headless/Research/FactorComputationTests.cs` | 71 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Research/HttpEnvResolverClientTests.cs` | 169 | win | TradingTerminal.Tests.Headless | dev | Y | Contract tests for the env-resolver HTTP client, mirroring HttpPaperIngestClientTests: the |
| `tests/TradingTerminal.Tests.Headless/Research/HttpPaperIngestClientTests.cs` | 137 | win | TradingTerminal.Tests.Headless | dev | Y | Contract tests for the paper-ingest HTTP client, mirroring HttpAiAnalystClientTests: the |
| `tests/TradingTerminal.Tests.Headless/Research/LocalReproOrchestratorTests.cs` | 264 | win | TradingTerminal.Tests.Headless | dev | Y | Returns a canned result immediately. Fails the test if invoked when not |
| `tests/TradingTerminal.Tests.Headless/Research/ReplicationConfidenceScorerTests.cs` | 72 | win | TradingTerminal.Tests.Headless | dev | Y | The replication-confidence scorer over canned results: a failed run scores 0, a |
| `tests/TradingTerminal.Tests.Headless/Research/ReproJobStoreTests.cs` | 105 | win | TradingTerminal.Tests.Headless | dev | Y | Round-trips for the SQLite reproduction job store: cache-key lookup (only succeeded jobs |
| `tests/TradingTerminal.Tests.Headless/Research/ReproSignalBridgeTests.cs` | 124 | win | TradingTerminal.Tests.Headless | dev | Y | The artifact → manifest bridge over a real on-disk result.json: snake_case parse |
| `tests/TradingTerminal.Tests.Headless/Research/ReproducedSignalKernelTests.cs` | 86 | win | TradingTerminal.Tests.Headless | dev | Y | Engine smoke test for the Phase-3 bridge endpoint: a canned |
| `tests/TradingTerminal.Tests.Headless/Research/SandboxPolicyTests.cs` | 57 | win | TradingTerminal.Tests.Headless | dev | Y | The sandbox policy is a security boundary: it must deny by default. |
| `tests/TradingTerminal.Tests.Headless/Risk/RiskManagerTests.cs` | 110 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Strategies/AuthoredStrategyComposerTests.cs` | 261 | win | TradingTerminal.Tests.Headless | dev | Y | Kernel + descriptor + live view-model, no view — the shape the |
| `tests/TradingTerminal.Tests.Headless/Strategies/IndexRegimeAggregatorTests.cs` | 103 | win | TradingTerminal.Tests.Headless | dev | Y | Snapshot whose every timeframe column carries the trend score returned by |
| `tests/TradingTerminal.Tests.Headless/Strategies/RoslynStrategyCompilerTests.cs` | 191 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Strategies/StrategyClassificationTests.cs` | 98 | win | TradingTerminal.Tests.Headless | dev | Y | Guards the classification defaults on and the broker-capability |
| `tests/TradingTerminal.Tests.Headless/Strategies/StrategyCodegenTests.cs` | 1082 | win | TradingTerminal.Tests.Headless | dev | Y | A kernel that compiles and does nothing — the fixture for the |
| `tests/TradingTerminal.Tests.Headless/Strategies/StrategyParametersTests.cs` | 128 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/TestSupport/ImmediateDispatcher.cs` | 11 | win | TradingTerminal.Tests.Headless | dev | Y | UI dispatcher stand-in for tests; runs everything inline on the calling thread. |
| `tests/TradingTerminal.Tests.Headless/Ui/StrategyCatalogViewModelTests.cs` | 38 | win | TradingTerminal.Tests.Headless | dev | Y | Tests the portable strategy-catalog VM (shared by the WPF + Avalonia heads). |
