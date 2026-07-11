# index/Tests — per-file index (Windows tree)

Generated 2026-07-11. Grep by filename/keyword. LOC > 400 => never read whole; rg then ranged reads.
Editions: B=Basic, I=Intermediate, P=Pro (private repo consumes this tree); dev=test-only.

| File | LOC | Tree | Project | Ed | Pub | Purpose |
|---|---|---|---|---|---|---|
| `tests/TradingTerminal.Tests/AiAnalyst/AiAnalystEnricherTests.cs` | 146 | win | TradingTerminal.Tests | dev | Y |  |
| `tests/TradingTerminal.Tests/AiAnalyst/HttpAiAnalystClientTests.cs` | 140 | win | TradingTerminal.Tests | dev | Y |  |
| `tests/TradingTerminal.Tests/AiAnalyst/NullAiAnalystClientTests.cs` | 37 | win | TradingTerminal.Tests | dev | Y |  |
| `tests/TradingTerminal.Tests/Backtest/DuckDbParquetQueryServiceTests.cs` | 98 | win | TradingTerminal.Tests | dev | Y | Exercises against real Parquet files produced by |
| `tests/TradingTerminal.Tests/Controls/InstrumentPickerFilterTests.cs` | 120 | win | TradingTerminal.Tests | dev | Y | Unit tests for — the shared logic behind every instrument |
| `tests/TradingTerminal.Tests/Controls/InstrumentPickerTests.cs` | 54 | win | TradingTerminal.Tests | dev | Y | Regression test for the strategy-window crash "Cannot find resource named |
| `tests/TradingTerminal.Tests/Login/LoginFormEditionCompositionTests.cs` | 98 | win | TradingTerminal.Tests | dev | Y | The broker-neutral services every edition provides before AddLogin. |
| `tests/TradingTerminal.Tests/MarketData/LocalParquetLakeExporterTests.cs` | 132 | win | TradingTerminal.Tests | dev | Y | Minimal hand-rolled |
| `tests/TradingTerminal.Tests/OrderFlowPressureMap/PressureMapCalculatorTests.cs` | 154 | win | TradingTerminal.Tests | dev | Y |  |
| `tests/TradingTerminal.Tests/Strategies/StrategyFactoryTests.cs` | 65 | win | TradingTerminal.Tests | dev | Y |  |
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
| `tests/TradingTerminal.Tests.Headless/Strategies/ApexScalperWarmupTests.cs` | 147 | win | TradingTerminal.Tests.Headless | dev | Y | A noisy upward trend so the line fits have non-zero residuals (else |
| `tests/TradingTerminal.Tests.Headless/Strategies/IndexRegimeAggregatorTests.cs` | 103 | win | TradingTerminal.Tests.Headless | dev | Y | Snapshot whose every timeframe column carries the trend score returned by |
| `tests/TradingTerminal.Tests.Headless/Strategies/RoslynStrategyCompilerTests.cs` | 100 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/Strategies/StrategyClassificationTests.cs` | 98 | win | TradingTerminal.Tests.Headless | dev | Y | Guards the classification defaults on and the broker-capability |
| `tests/TradingTerminal.Tests.Headless/Strategies/StrategyParametersTests.cs` | 128 | win | TradingTerminal.Tests.Headless | dev | Y |  |
| `tests/TradingTerminal.Tests.Headless/TestSupport/ImmediateDispatcher.cs` | 11 | win | TradingTerminal.Tests.Headless | dev | Y | UI dispatcher stand-in for tests; runs everything inline on the calling thread. |
| `tests/TradingTerminal.Tests.Headless/Ui/PortedStrategyResolutionTests.cs` | 77 | win | TradingTerminal.Tests.Headless | dev | Y | Proves the ported per-strategy view-models resolve from the same headless DI graph |
| `tests/TradingTerminal.Tests.Headless/Ui/StrategyCatalogViewModelTests.cs` | 38 | win | TradingTerminal.Tests.Headless | dev | Y | Tests the portable strategy-catalog VM (shared by the WPF + Avalonia heads). |
