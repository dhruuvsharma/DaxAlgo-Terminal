# Linux index / Tests

Generated from public repository revision `b73fd6a72ae2`. Linux/Avalonia slice only.

| File | LOC | Tree | Project | Role | Public surface | Purpose |
|---|---:|---|---|---|---|---|
| `tests/linux/TradingTerminal.Tests.Headless/Analytics/CorrelationCalculatorTests.cs` | 103 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Backtest/BacktestSessionTests.cs` | 106 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Backtest/BacktestStoreSourceTests.cs` | 197 | linux | TradingTerminal.Tests.Headless | test | Y | Minimal hand-rolled |
| `tests/linux/TradingTerminal.Tests.Headless/Backtest/FeeModelTests.cs` | 88 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Backtest/MonteCarloTests.cs` | 56 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Backtest/ParquetTickRoundtripTests.cs` | 65 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Backtest/ParquetTradeTapeTests.cs` | 79 | linux | TradingTerminal.Tests.Headless | test | Y | Covers the optional backtest trade tape: trades round-trip through parquet with their |
| `tests/linux/TradingTerminal.Tests.Headless/Backtest/StatisticsCalculatorTests.cs` | 88 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Backtest/TransactionCostAnalysisTests.cs` | 86 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Backtesting/BacktestEngineTests.cs` | 78 | linux | TradingTerminal.Tests.Headless | test | Y | End-to-end smoke + accounting checks for the new (P1). Drives an |
| `tests/linux/TradingTerminal.Tests.Headless/Backtesting/EngineParityTests.cs` | 101 | linux | TradingTerminal.Tests.Headless | test | Y | Parity gate: the new must reproduce the old |
| `tests/linux/TradingTerminal.Tests.Headless/Backtesting/GeneticAndWalkForwardTests.cs` | 71 | linux | TradingTerminal.Tests.Headless | test | Y | Covers the genetic optimizer and walk-forward analysis built on the new engine. |
| `tests/linux/TradingTerminal.Tests.Headless/Backtesting/GpuFallbackTests.cs` | 62 | linux | TradingTerminal.Tests.Headless | test | Y | Covers the C# side of the GPU accelerator: the support gate and |
| `tests/linux/TradingTerminal.Tests.Headless/Backtesting/GpuParityTests.cs` | 67 | linux | TradingTerminal.Tests.Headless | test | Y | Validates the CUDA accelerator against the CPU optimizer: the GPU's net profit |
| `tests/linux/TradingTerminal.Tests.Headless/Backtesting/KernelRegistryTests.cs` | 73 | linux | TradingTerminal.Tests.Headless | test | Y | Covers kernel discovery by id, schema-driven defaults/clamping, and that a registry-built |
| `tests/linux/TradingTerminal.Tests.Headless/Backtesting/LegacyBridgeTests.cs` | 42 | linux | TradingTerminal.Tests.Headless | test | Y | Cutover check: a legacy |
| `tests/linux/TradingTerminal.Tests.Headless/Backtesting/OptimizerTests.cs` | 74 | linux | TradingTerminal.Tests.Headless | test | Y | Covers the grid optimizer: Cartesian expansion, that it evaluates every combination, |
| `tests/linux/TradingTerminal.Tests.Headless/Backtesting/PythonStrategyTests.cs` | 72 | linux | TradingTerminal.Tests.Headless | test | Y | End-to-end check that a Python-authored strategy (daxalgo_bt example) runs on the new |
| `tests/linux/TradingTerminal.Tests.Headless/Backtesting/StoreFeedAndPortfolioTests.cs` | 95 | linux | TradingTerminal.Tests.Headless | test | Y | Exercises the store-backed feed's k-way merge and the engine's multi-instrument (portfolio) |
| `tests/linux/TradingTerminal.Tests.Headless/Backtesting/VisualTimelineTests.cs` | 63 | linux | TradingTerminal.Tests.Headless | test | Y | Verifies the engine captures a visual timeline only when asked, with OHLC |
| `tests/linux/TradingTerminal.Tests.Headless/Infrastructure/BinanceParsingTests.cs` | 207 | linux | TradingTerminal.Tests.Headless | test | Y | Offline tests for the Binance public-feed JSON parsers. No network — they |
| `tests/linux/TradingTerminal.Tests.Headless/Infrastructure/ConnectionManagerTests.cs` | 209 | linux | TradingTerminal.Tests.Headless | test | Y | ConnectAsync returns normally but reports Failed every time (never Connected) — |
| `tests/linux/TradingTerminal.Tests.Headless/Infrastructure/IronBeamParsingTests.cs` | 180 | linux | TradingTerminal.Tests.Headless | test | Y | Offline tests for the Ironbeam WebSocket JSON parsers and symbol mapping. No |
| `tests/linux/TradingTerminal.Tests.Headless/Infrastructure/LondonStrategicEdgeParsingTests.cs` | 185 | linux | TradingTerminal.Tests.Headless | test | Y | Offline tests for the London Strategic Edge WebSocket/REST JSON parsers and symbol |
| `tests/linux/TradingTerminal.Tests.Headless/Infrastructure/SimulatedBrokerClientTests.cs` | 138 | linux | TradingTerminal.Tests.Headless | test | Y | Base test store: writes are swallowed and reads return empty unless a |
| `tests/linux/TradingTerminal.Tests.Headless/Infrastructure/UpstoxParsingTests.cs` | 148 | linux | TradingTerminal.Tests.Headless | test | Y | Tests for the Upstox parsing helpers. The protobuf test encodes a FeedResponse |
| `tests/linux/TradingTerminal.Tests.Headless/MarketData/ArchiveDepthRoundTripTests.cs` | 178 | linux | TradingTerminal.Tests.Headless | test | Y | In-memory |
| `tests/linux/TradingTerminal.Tests.Headless/MarketData/IndicatorsTests.cs` | 70 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/MarketData/InstrumentDiscoveryServiceTests.cs` | 136 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/MarketData/InstrumentRegistryTests.cs` | 72 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/MarketData/MarketDataHubTests.cs` | 52 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/MarketData/MarketDataIngestServiceTests.cs` | 116 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/MarketData/MarketDataRepositoryTests.cs` | 232 | linux | TradingTerminal.Tests.Headless | test | Y | Test selector that returns the single supplied client for whichever broker is |
| `tests/linux/TradingTerminal.Tests.Headless/MarketData/MicrostructureTests.cs` | 126 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/MarketData/NpgsqlMarketDataStoreTests.cs` | 67 | linux | TradingTerminal.Tests.Headless | test | Y | Integration tests against the docker-compose TimescaleDB. They self-skip (return) when the |
| `tests/linux/TradingTerminal.Tests.Headless/MarketData/OrderFlowImbalanceTests.cs` | 82 | linux | TradingTerminal.Tests.Headless | test | Y | Unit tests for the OBI(T) primitives from arXiv:2507.22712 (trade-based imbalance + the |
| `tests/linux/TradingTerminal.Tests.Headless/MarketData/PerBrokerSqliteMarketDataStoreTests.cs` | 179 | linux | TradingTerminal.Tests.Headless | test | Y | Behaviour of the per-broker, per-stream SQLite store: writes route to one file |
| `tests/linux/TradingTerminal.Tests.Headless/MarketData/QuestDbMarketDataStoreTests.cs` | 124 | linux | TradingTerminal.Tests.Headless | test | Y | Poll a read until it returns the expected count or the timeout |
| `tests/linux/TradingTerminal.Tests.Headless/MarketData/SqliteMarketDataStoreTests.cs` | 131 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Ml/OnlineLinearRegressionTests.cs` | 61 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Ml/TripleBarrierLabelerTests.cs` | 77 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Quant/CurveFittingTests.cs` | 177 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Quant/DeflatedSharpeTests.cs` | 101 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Quant/EwRegressionTests.cs` | 143 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Quant/FirstPassageTests.cs` | 117 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Quant/FootprintFeaturesTests.cs` | 278 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Quant/HawkesProcessTests.cs` | 60 | linux | TradingTerminal.Tests.Headless | test | Y | Covers the Hawkes self-exciting intensity tracker: it returns the baseline with no |
| `tests/linux/TradingTerminal.Tests.Headless/Quant/InformationCoefficientTests.cs` | 114 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Quant/IsotonicCalibrationTests.cs` | 147 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Quant/KalmanPocPredictorTests.cs` | 66 | linux | TradingTerminal.Tests.Headless | test | Y | Covers the constant-velocity Kalman POC predictor: it should lock onto a linear |
| `tests/linux/TradingTerminal.Tests.Headless/Quant/KyleResidualTests.cs` | 111 | linux | TradingTerminal.Tests.Headless | test | Y | Generates AR(1) signed flow Δ_t = φ·Δ_{t−1} + u_t so the lagged |
| `tests/linux/TradingTerminal.Tests.Headless/Quant/LedoitWolfTests.cs` | 204 | linux | TradingTerminal.Tests.Headless | test | Y | Generates n samples from a k-variate normal with the given (lower-triangular) Cholesky |
| `tests/linux/TradingTerminal.Tests.Headless/Quant/NeweyWestTests.cs` | 164 | linux | TradingTerminal.Tests.Headless | test | Y | Naive OLS slope SE for a simple regression: sqrt( Σe²/(n-2) / S_xx |
| `tests/linux/TradingTerminal.Tests.Headless/Quant/QuantTestRandom.cs` | 17 | linux | TradingTerminal.Tests.Headless | test | Y | Standard-normal draw (mean 0, sd 1) via Box-Muller. |
| `tests/linux/TradingTerminal.Tests.Headless/Quant/SignalWeightsTests.cs` | 118 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Quant/TimeSeriesMathTests.cs` | 303 | linux | TradingTerminal.Tests.Headless | test | Y | Offline tests for the Machine Learning menu's time-series math in Core — |
| `tests/linux/TradingTerminal.Tests.Headless/Quant/VolumeTimeBucketerTests.cs` | 157 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Regime/AdvancedRegimeCalculatorTests.cs` | 286 | linux | TradingTerminal.Tests.Headless | test | Y | Strictly rising up-candles, 1m apart, all within one UTC session day. |
| `tests/linux/TradingTerminal.Tests.Headless/Regime/BarTimeframeAggregatorTests.cs` | 87 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Regime/MarketRegimeCalculatorTests.cs` | 116 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Research/DockerSandboxRunnerTests.cs` | 82 | linux | TradingTerminal.Tests.Headless | test | Y | Isolation/quota integration tests for the Docker sandbox runner. They SELF-SKIP (return) when |
| `tests/linux/TradingTerminal.Tests.Headless/Research/FactorComputationTests.cs` | 71 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Research/HttpEnvResolverClientTests.cs` | 169 | linux | TradingTerminal.Tests.Headless | test | Y | Contract tests for the env-resolver HTTP client, mirroring HttpPaperIngestClientTests: the |
| `tests/linux/TradingTerminal.Tests.Headless/Research/HttpPaperIngestClientTests.cs` | 137 | linux | TradingTerminal.Tests.Headless | test | Y | Contract tests for the paper-ingest HTTP client, mirroring HttpAiAnalystClientTests: the |
| `tests/linux/TradingTerminal.Tests.Headless/Research/LocalReproOrchestratorTests.cs` | 264 | linux | TradingTerminal.Tests.Headless | test | Y | Returns a canned result immediately. Fails the test if invoked when not |
| `tests/linux/TradingTerminal.Tests.Headless/Research/ReplicationConfidenceScorerTests.cs` | 72 | linux | TradingTerminal.Tests.Headless | test | Y | The replication-confidence scorer over canned results: a failed run scores 0, a |
| `tests/linux/TradingTerminal.Tests.Headless/Research/ReproJobStoreTests.cs` | 105 | linux | TradingTerminal.Tests.Headless | test | Y | Round-trips for the SQLite reproduction job store: cache-key lookup (only succeeded jobs |
| `tests/linux/TradingTerminal.Tests.Headless/Research/ReproSignalBridgeTests.cs` | 124 | linux | TradingTerminal.Tests.Headless | test | Y | The artifact → manifest bridge over a real on-disk result.json: snake_case parse |
| `tests/linux/TradingTerminal.Tests.Headless/Research/ReproducedSignalKernelTests.cs` | 86 | linux | TradingTerminal.Tests.Headless | test | Y | Engine smoke test for the Phase-3 bridge endpoint: a canned |
| `tests/linux/TradingTerminal.Tests.Headless/Research/SandboxPolicyTests.cs` | 57 | linux | TradingTerminal.Tests.Headless | test | Y | The sandbox policy is a security boundary: it must deny by default. |
| `tests/linux/TradingTerminal.Tests.Headless/Risk/RiskManagerTests.cs` | 110 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Strategies/ApexScalperWarmupTests.cs` | 147 | linux | TradingTerminal.Tests.Headless | test | Y | A noisy upward trend so the line fits have non-zero residuals (else |
| `tests/linux/TradingTerminal.Tests.Headless/Strategies/IndexRegimeAggregatorTests.cs` | 103 | linux | TradingTerminal.Tests.Headless | test | Y | Snapshot whose every timeframe column carries the trend score returned by |
| `tests/linux/TradingTerminal.Tests.Headless/Strategies/RoslynStrategyCompilerTests.cs` | 100 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/Strategies/StrategyClassificationTests.cs` | 98 | linux | TradingTerminal.Tests.Headless | test | Y | Guards the classification defaults on and the broker-capability |
| `tests/linux/TradingTerminal.Tests.Headless/Strategies/StrategyParametersTests.cs` | 128 | linux | TradingTerminal.Tests.Headless | test | Y |  |
| `tests/linux/TradingTerminal.Tests.Headless/TestSupport/ImmediateDispatcher.cs` | 11 | linux | TradingTerminal.Tests.Headless | test | Y | UI dispatcher stand-in for tests; runs everything inline on the calling thread. |
| `tests/linux/TradingTerminal.Tests.Headless/Ui/PortedStrategyResolutionTests.cs` | 77 | linux | TradingTerminal.Tests.Headless | test | Y | Proves the ported per-strategy view-models resolve from the same headless DI graph |
| `tests/linux/TradingTerminal.Tests.Headless/Ui/StrategyCatalogViewModelTests.cs` | 38 | linux | TradingTerminal.Tests.Headless | test | Y | Tests the portable strategy-catalog VM (shared by the WPF + Avalonia heads). |
