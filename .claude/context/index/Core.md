# index/Core — per-file index (Windows tree)

Generated 2026-07-17. Grep by filename/keyword. LOC > 400 => never read whole; rg then ranged reads.
Editions: B=Basic, I=Intermediate, P=Pro (private repo consumes this tree); dev=test-only.

| File | LOC | Tree | Project | Ed | Pub | Purpose |
|---|---|---|---|---|---|---|
| `src/windows/Core/TradingTerminal.Core/AiAnalyst/AiAnalystDecision.cs` | 9 | win | TradingTerminal.Core | B I P | Y | No actionable verdict — the analyst either declined to call or was |
| `src/windows/Core/TradingTerminal.Core/AiAnalyst/AnalystBar.cs` | 13 | win | TradingTerminal.Core | B I P | Y | Wire-format OHLCV bar handed to the Python analyst. Mirrors Bar but lives |
| `src/windows/Core/TradingTerminal.Core/AiAnalyst/AnalystReport.cs` | 68 | win | TradingTerminal.Core | B I P | Y | One indicator agent's read on the tape — text summary plus the |
| `src/windows/Core/TradingTerminal.Core/AiAnalyst/AnalystRequest.cs` | 15 | win | TradingTerminal.Core | B I P | Y | Payload sent to the Python sidecar's /analyst/run endpoint. Keeps provider / |
| `src/windows/Core/TradingTerminal.Core/AiAnalyst/IAiAnalystClient.cs` | 20 | win | TradingTerminal.Core | B I P | Y | True when this client expects the sidecar to be reachable. The Null |
| `src/windows/Core/TradingTerminal.Core/Analytics/CorrelationCalculator.cs` | 126 | win | TradingTerminal.Core | B I P | Y | Bar-to-bar log returns |
| `src/windows/Core/TradingTerminal.Core/Analytics/CorrelationResult.cs` | 18 | win | TradingTerminal.Core | B I P | Y | A computed correlation matrix over a set of instruments. indexes both |
| `src/windows/Core/TradingTerminal.Core/Backtest/BacktestConfig.cs` | 50 | win | TradingTerminal.Core | B I P | Y | Where the engine pulls tick data from for a single backtest run. |
| `src/windows/Core/TradingTerminal.Core/Backtest/BacktestResult.cs` | 15 | win | TradingTerminal.Core | B I P | Y | Output of a single backtest run. is null until Phase 4 wires |
| `src/windows/Core/TradingTerminal.Core/Backtest/BacktestStatistics.cs` | 35 | win | TradingTerminal.Core | B I P | Y | Aggregate performance metrics derived from a 's trades and |
| `src/windows/Core/TradingTerminal.Core/Backtest/BacktestStrategyOption.cs` | 78 | win | TradingTerminal.Core | B I P | Y | Declared tunables. |
| `src/windows/Core/TradingTerminal.Core/Backtest/EquityPoint.cs` | 4 | win | TradingTerminal.Core | B I P | Y | Sample of the equity curve at a point in time. |
| `src/windows/Core/TradingTerminal.Core/Backtest/Fast/FastBacktestRequest.cs` | 26 | win | TradingTerminal.Core | B I P | Y | Input to the out-of-process C++ tick backtester. Serialised to JSON, written to |
| `src/windows/Core/TradingTerminal.Core/Backtest/Fast/FastBacktestResult.cs` | 18 | win | TradingTerminal.Core | B I P | Y | Result emitted by the C++ tick backtester on stdout as JSON. The |
| `src/windows/Core/TradingTerminal.Core/Backtest/Fast/IFastBacktestRunner.cs` | 25 | win | TradingTerminal.Core | B I P | Y | Out-of-process replay engine. Runs in a separate subprocess (the C++20 |
| `src/windows/Core/TradingTerminal.Core/Backtest/FillRecord.cs` | 17 | win | TradingTerminal.Core | B I P | Y | One fill captured during a backtest. Used by transaction-cost analysis |
| `src/windows/Core/TradingTerminal.Core/Backtest/IBacktestSession.cs` | 21 | win | TradingTerminal.Core | B I P | Y | View-model-facing seam over the backtest engine. The Backtest tab's view-model injects |
| `src/windows/Core/TradingTerminal.Core/Backtest/IBacktestStrategy.cs` | 50 | win | TradingTerminal.Core | B I P | Y | Called once before any ticks. Use to read initial state or schedule |
| `src/windows/Core/TradingTerminal.Core/Backtest/IParquetQueryService.cs` | 68 | win | TradingTerminal.Core | B I P | Y | One resampled bar from |
| `src/windows/Core/TradingTerminal.Core/Backtest/MonteCarlo.cs` | 147 | win | TradingTerminal.Core | B I P | Y | Trade-bootstrap Monte Carlo. Given a sequence of round-trip trade PnLs from a |
| `src/windows/Core/TradingTerminal.Core/Backtest/Trade.cs` | 16 | win | TradingTerminal.Core | B I P | Y | A round-trip trade: an entry fill and the matching exit fill that |
| `src/windows/Core/TradingTerminal.Core/Backtest/TransactionCostAnalysis.cs` | 112 | win | TradingTerminal.Core | B I P | Y | Transaction-cost analysis (TCA) — the standard post-trade report quant desks run to |
| `src/windows/Core/TradingTerminal.Core/Backtest/WalkForward.cs` | 34 | win | TradingTerminal.Core | B I P | Y | All-empty axes with quantity 1 — every strategy grid falls back to |
| `src/windows/Core/TradingTerminal.Core/Backtesting/BacktestReport.cs` | 111 | win | TradingTerminal.Core | B I P | Y | One sample of the account through time: mark-to-market |
| `src/windows/Core/TradingTerminal.Core/Backtesting/IStrategyContext.cs` | 31 | win | TradingTerminal.Core | B I P | Y | Simulated clock in backtests, wall clock live. Never call |
| `src/windows/Core/TradingTerminal.Core/Backtesting/IStrategyKernel.cs` | 45 | win | TradingTerminal.Core | B I P | Y | Called once before any market data. Read parameters, allocate per-instrument state. |
| `src/windows/Core/TradingTerminal.Core/Backtesting/Optimization.cs` | 111 | win | TradingTerminal.Core | B I P | Y | What the optimizer ranks trials by. Every criterion is scored so that |
| `src/windows/Core/TradingTerminal.Core/Backtesting/Portfolio.cs` | 44 | win | TradingTerminal.Core | B I P | Y | Free cash (realized PnL net of fees), excluding open-position marks. |
| `src/windows/Core/TradingTerminal.Core/Backtesting/RunSpec.cs` | 114 | win | TradingTerminal.Core | B I P | Y | Where the engine pulls historical market data from. |
| `src/windows/Core/TradingTerminal.Core/Backtesting/StrategyKernelRegistry.cs` | 59 | win | TradingTerminal.Core | B I P | Y | Discovers strategy kernels by id and builds instances. The Studio catalog, the |
| `src/windows/Core/TradingTerminal.Core/Backtesting/StrategyParameterSchema.cs` | 65 | win | TradingTerminal.Core | B I P | Y | The domain of a tunable parameter — drives both UI controls and |
| `src/windows/Core/TradingTerminal.Core/Backtesting/StrategyParameters.cs` | 44 | win | TradingTerminal.Core | B I P | Y | Reads a required parameter; throws |
| `src/windows/Core/TradingTerminal.Core/Backtesting/Universe.cs` | 39 | win | TradingTerminal.Core | B I P | Y | The first instrument — convenient for single-instrument runs and as a default |
| `src/windows/Core/TradingTerminal.Core/Backtesting/VisualTimeline.cs` | 24 | win | TradingTerminal.Core | B I P | Y | One OHLC candle of the charted instrument, aggregated from quote mids over |
| `src/windows/Core/TradingTerminal.Core/Brokers/BrokerApiUsage.cs` | 22 | win | TradingTerminal.Core | B I P | Y | Snapshot of one broker's API-call activity, as reported by . |
| `src/windows/Core/TradingTerminal.Core/Brokers/BrokerConnectionMode.cs` | 16 | win | TradingTerminal.Core | B I P | Y | Resolved at DI registration so the UI can tell whether a real |
| `src/windows/Core/TradingTerminal.Core/Brokers/BrokerKind.cs` | 89 | win | TradingTerminal.Core | B I P | Y | In-process synthetic / replay backend — no broker, no network. Streams either |
| `src/windows/Core/TradingTerminal.Core/Brokers/CTrader/CTraderDiscoveredAccount.cs` | 11 | win | TradingTerminal.Core | B I P | Y | A single cTrader trading account associated with an OAuth access token, as |
| `src/windows/Core/TradingTerminal.Core/Brokers/CTrader/ICTraderAccountDiscovery.cs` | 28 | win | TradingTerminal.Core | B I P | Y | One-shot helper used by the login form to enumerate the trading accounts |
| `src/windows/Core/TradingTerminal.Core/Brokers/IBrokerApiMeter.cs` | 19 | win | TradingTerminal.Core | B I P | Y | Record one API call against the named broker. Cheap; runs on the |
| `src/windows/Core/TradingTerminal.Core/Brokers/IBrokerLoginForm.cs` | 61 | win | TradingTerminal.Core | B I P | Y | True when all required fields are populated and the user may submit. |
| `src/windows/Core/TradingTerminal.Core/Brokers/IBrokerLoginFormFactory.cs` | 15 | win | TradingTerminal.Core | B I P | Y | Every form whose broker is currently available (i.e. its SDK was present |
| `src/windows/Core/TradingTerminal.Core/Brokers/IBrokerSelector.cs` | 55 | win | TradingTerminal.Core | B I P | Y | Brokers that have a registered |
| `src/windows/Core/TradingTerminal.Core/Brokers/Upstox/IUpstoxAuthService.cs` | 31 | win | TradingTerminal.Core | B I P | Y | One-shot helper for the Upstox login form's OAuth2 authorization-code flow. The interface |
| `src/windows/Core/TradingTerminal.Core/Configuration/AiCodegenOptions.cs` | 72 | win | TradingTerminal.Core | B I P | Y | Config for one BYO-key / local codegen provider. |
| `src/windows/Core/TradingTerminal.Core/Configuration/AlpacaOptions.cs` | 34 | win | TradingTerminal.Core | B I P | Y | API key id (shown on the dashboard, prefixed with "PK" for paper |
| `src/windows/Core/TradingTerminal.Core/Configuration/AppEdition.cs` | 22 | win | TradingTerminal.Core | B I P | Y | Keyless brokers only, core charts/tools/strategies. No ML / AI / LSE / |
| `src/windows/Core/TradingTerminal.Core/Configuration/ArchiveOptions.cs` | 51 | win | TradingTerminal.Core | B I P | Y | Master switch — when false the schedule service idles and the offload |
| `src/windows/Core/TradingTerminal.Core/Configuration/BinanceOptions.cs` | 45 | win | TradingTerminal.Core | B I P | Y | REST base for the ping/connectivity check and historical klines. No trailing slash. |
| `src/windows/Core/TradingTerminal.Core/Configuration/BrokerEditionPolicy.cs` | 49 | win | TradingTerminal.Core | B I P | Y | Brokers usable with no API key or account. Available in every edition. |
| `src/windows/Core/TradingTerminal.Core/Configuration/BybitOptions.cs` | 36 | win | TradingTerminal.Core | B I P | Y | REST base for historical kline + the connectivity check. No trailing slash. |
| `src/windows/Core/TradingTerminal.Core/Configuration/CTraderOptions.cs` | 35 | win | TradingTerminal.Core | B I P | Y | OAuth application clientId (from connect.spotware.com/apps). |
| `src/windows/Core/TradingTerminal.Core/Configuration/CoinbaseOptions.cs` | 34 | win | TradingTerminal.Core | B I P | Y | REST base for historical candles + the connectivity check. No trailing slash. |
| `src/windows/Core/TradingTerminal.Core/Configuration/DevOptions.cs` | 28 | win | TradingTerminal.Core | B I P | Y | Developer-only switches, bound from the Dev configuration section. These are off by |
| `src/windows/Core/TradingTerminal.Core/Configuration/InteractiveBrokersOptions.cs` | 24 | win | TradingTerminal.Core | B I P | Y | IB market-data subscription mode applied to all reqMktData requests. |
| `src/windows/Core/TradingTerminal.Core/Configuration/IronBeamOptions.cs` | 42 | win | TradingTerminal.Core | B I P | Y | Ironbeam account username. |
| `src/windows/Core/TradingTerminal.Core/Configuration/KrakenOptions.cs` | 34 | win | TradingTerminal.Core | B I P | Y | REST base for historical OHLC + the connectivity check. No trailing slash. |
| `src/windows/Core/TradingTerminal.Core/Configuration/LondonStrategicEdgeOptions.cs` | 38 | win | TradingTerminal.Core | B I P | Y | API key from londonstrategicedge.com/websockets (format |
| `src/windows/Core/TradingTerminal.Core/Configuration/MarketDataStoreOptions.cs` | 122 | win | TradingTerminal.Core | B I P | Y | Which backend persists the canonical market-data store. |
| `src/windows/Core/TradingTerminal.Core/Configuration/MarketRegimeOptions.cs` | 51 | win | TradingTerminal.Core | B I P | Y | Master switch. When false the service does not poll and the panel |
| `src/windows/Core/TradingTerminal.Core/Configuration/ModelRegistryOptions.cs` | 19 | win | TradingTerminal.Core | B I P | Y | SQLite database file path. Empty → a default ( |
| `src/windows/Core/TradingTerminal.Core/Configuration/NinjaTraderOptions.cs` | 31 | win | TradingTerminal.Core | B I P | Y | NinjaTrader account name (e.g. "Sim101" for the bundled simulation account). |
| `src/windows/Core/TradingTerminal.Core/Configuration/OkxOptions.cs` | 31 | win | TradingTerminal.Core | B I P | Y | REST base for historical candles + the connectivity check. No trailing slash. |
| `src/windows/Core/TradingTerminal.Core/Configuration/OrderFlowPressureMapOptions.cs` | 129 | win | TradingTerminal.Core | B I P | Y | No notable pressure this candle. |
| `src/windows/Core/TradingTerminal.Core/Configuration/ParquetLakeOptions.cs` | 34 | win | TradingTerminal.Core | B I P | Y | Master switch. When false the export service idles (one cheap timer tick |
| `src/windows/Core/TradingTerminal.Core/Configuration/PluginsOptions.cs` | 63 | win | TradingTerminal.Core | B I P | Y | How much the host trusts the plugins it finds in its plugins |
| `src/windows/Core/TradingTerminal.Core/Configuration/ResearchReproOptions.cs` | 36 | win | TradingTerminal.Core | B I P | Y | Master switch. When false the ingest client is Null and no jobs |
| `src/windows/Core/TradingTerminal.Core/Configuration/SandboxOptions.cs` | 39 | win | TradingTerminal.Core | B I P | Y | Which runner implementation |
| `src/windows/Core/TradingTerminal.Core/Configuration/SidecarOptions.cs` | 31 | win | TradingTerminal.Core | B I P | Y | Master switch. When true, the app auto-launches the sidecar on startup if |
| `src/windows/Core/TradingTerminal.Core/Configuration/SimulatedBrokerOptions.cs` | 69 | win | TradingTerminal.Core | B I P | Y | How the |
| `src/windows/Core/TradingTerminal.Core/Configuration/TelegramArchiveOptions.cs` | 37 | win | TradingTerminal.Core | B I P | Y | From https://my.telegram.org/apps. Required for any Telegram operation. |
| `src/windows/Core/TradingTerminal.Core/Configuration/UpstoxOptions.cs` | 46 | win | TradingTerminal.Core | B I P | Y | OAuth2 client id — the Upstox app's "API Key" from the developer |
| `src/windows/Core/TradingTerminal.Core/Domain/AssetClass.cs` | 17 | win | TradingTerminal.Core | B I P | Y | Broker-neutral asset classification for a canonical instrument. Derived from a broker's |
| `src/windows/Core/TradingTerminal.Core/Domain/Bar.cs` | 10 | win | TradingTerminal.Core | B I P | Y | An OHLCV bar at a specific UTC timestamp (the bar's open time). |
| `src/windows/Core/TradingTerminal.Core/Domain/BarSize.cs` | 59 | win | TradingTerminal.Core | B I P | Y | The exact string the TWS API expects for |
| `src/windows/Core/TradingTerminal.Core/Domain/ConnectionState.cs` | 10 | win | TradingTerminal.Core | B I P | Y |  |
| `src/windows/Core/TradingTerminal.Core/Domain/Contract.cs` | 13 | win | TradingTerminal.Core | B I P | Y | An IB-style instrument descriptor (intentionally aligned with TWS API fields). |
| `src/windows/Core/TradingTerminal.Core/Domain/DepthLevel.cs` | 8 | win | TradingTerminal.Core | B I P | Y | A single price level on one side of the order book. Sizes |
| `src/windows/Core/TradingTerminal.Core/Domain/DepthSnapshot.cs` | 30 | win | TradingTerminal.Core | B I P | Y | Best (highest) bid, or 0 when the bid side is empty. |
| `src/windows/Core/TradingTerminal.Core/Domain/Instrument.cs` | 41 | win | TradingTerminal.Core | B I P | Y | Builds an as-yet-unpersisted instrument (id = None) for the registry to insert. |
| `src/windows/Core/TradingTerminal.Core/Domain/InstrumentId.cs` | 18 | win | TradingTerminal.Core | B I P | Y | The unset / unresolved id. Ingest treats a record carrying this as |
| `src/windows/Core/TradingTerminal.Core/Domain/MarketDataRecords.cs` | 81 | win | TradingTerminal.Core | B I P | Y | Which side initiated a trade print, when the broker reports it. |
| `src/windows/Core/TradingTerminal.Core/Domain/Tick.cs` | 26 | win | TradingTerminal.Core | B I P | Y | A single bid/ask quote update from IB's tick-by-tick BidAsk feed. Sizes are |
| `src/windows/Core/TradingTerminal.Core/Events/EventBus.cs` | 44 | win | TradingTerminal.Core | B I P | Y |  |
| `src/windows/Core/TradingTerminal.Core/Events/IEventBus.cs` | 11 | win | TradingTerminal.Core | B I P | Y | Lightweight in-process pub/sub. Use for cross-pane events (strategy opened, connection lost, etc.). |
| `src/windows/Core/TradingTerminal.Core/Hosting/ISidecarController.cs` | 17 | win | TradingTerminal.Core | B I P | Y | True once the sidecar's health endpoint has answered. |
| `src/windows/Core/TradingTerminal.Core/Hosting/NullSidecarController.cs` | 15 | win | TradingTerminal.Core | B I P | Y | No sidecar in this edition — returns false without starting anything, never |
| `src/windows/Core/TradingTerminal.Core/IndexKScore/IndexComponentCatalog.cs` | 123 | win | TradingTerminal.Core | B I P | Y | A named index universe: its display metadata and weighted constituents. |
| `src/windows/Core/TradingTerminal.Core/IndexKScore/IndexKScoreAggregator.cs` | 174 | win | TradingTerminal.Core | B I P | Y | Updates the component snapshot and returns the new index-level aggregate. Returns |
| `src/windows/Core/TradingTerminal.Core/IndexKScore/IndexKScoreCalculator.cs` | 456 | win | TradingTerminal.Core | B I P | Y | One bar's worth of computed signals plus the composite K. Returned by |
| `src/windows/Core/TradingTerminal.Core/IndexKScore/IndexKScoreParameters.cs` | 79 | win | TradingTerminal.Core | B I P | Y | Configuration for the per-component K-score computation. All windows / lookbacks / |
| `src/windows/Core/TradingTerminal.Core/IndexRegime/IndexRegimeAggregator.cs` | 115 | win | TradingTerminal.Core | B I P | Y | Maps a signed score in |
| `src/windows/Core/TradingTerminal.Core/IndexRegime/IndexRegimeModels.cs` | 58 | win | TradingTerminal.Core | B I P | Y | One constituent timeframe column collapsed to a signed score in |
| `src/windows/Core/TradingTerminal.Core/IndexRegime/RegimeHorizon.cs` | 72 | win | TradingTerminal.Core | B I P | Y | Minutes — weights the fastest columns. |
| `src/windows/Core/TradingTerminal.Core/MarketData/AdvancedRegime/AdvancedRegimeBarIndicators.cs` | 314 | win | TradingTerminal.Core | B I P | Y | SMA over the final |
| `src/windows/Core/TradingTerminal.Core/MarketData/AdvancedRegime/AdvancedRegimeCalculator.cs` | 346 | win | TradingTerminal.Core | B I P | Y | Final-bar Wilder RSI via the shared streaming primitive; NaN if not warmed |
| `src/windows/Core/TradingTerminal.Core/MarketData/AdvancedRegime/AdvancedRegimeModels.cs` | 101 | win | TradingTerminal.Core | B I P | Y | The eight default timeframe columns, all enabled. |
| `src/windows/Core/TradingTerminal.Core/MarketData/AdvancedRegime/AdvancedRegimeSettings.cs` | 106 | win | TradingTerminal.Core | B I P | Y | Whether the given row is enabled, by enum value. |
| `src/windows/Core/TradingTerminal.Core/MarketData/AdvancedRegime/BarTimeframeAggregator.cs` | 87 | win | TradingTerminal.Core | B I P | Y | Pure bar-to-bar timeframe aggregator. Resamples a time-ascending base bar series into wider |
| `src/windows/Core/TradingTerminal.Core/MarketData/AdvancedRegime/IAdvancedRegimeProvider.cs` | 24 | win | TradingTerminal.Core | B I P | Y | Multi-timeframe regime dashboard provider. Mirrors IInstrumentRegimeProvider in shape: |
| `src/windows/Core/TradingTerminal.Core/MarketData/Archive/ArchiveModels.cs` | 149 | win | TradingTerminal.Core | B I P | Y | How often the schedule rolls over and produces a new archive. |
| `src/windows/Core/TradingTerminal.Core/MarketData/Archive/IArchiveTransport.cs` | 35 | win | TradingTerminal.Core | B I P | Y | Human-readable name of this transport, persisted into manifests so the right |
| `src/windows/Core/TradingTerminal.Core/MarketData/Archive/IMarketDataArchiver.cs` | 40 | win | TradingTerminal.Core | B I P | Y | Export, upload, verify, and (per options) prune local rows for the range. |
| `src/windows/Core/TradingTerminal.Core/MarketData/Archive/ITelegramArchiveLogin.cs` | 36 | win | TradingTerminal.Core | B I P | Y | Outcome of a |
| `src/windows/Core/TradingTerminal.Core/MarketData/CuratedInstrumentCatalog.cs` | 83 | win | TradingTerminal.Core | B I P | Y | ETFs, large-cap stocks, continuous futures and FX — a broad starter set |
| `src/windows/Core/TradingTerminal.Core/MarketData/FootprintFeatures.cs` | 334 | win | TradingTerminal.Core | B I P | Y | No usable feed. |
| `src/windows/Core/TradingTerminal.Core/MarketData/FootprintTimeBucketer.cs` | 82 | win | TradingTerminal.Core | B I P | Y | Start of the bucket currently accumulating, or |
| `src/windows/Core/TradingTerminal.Core/MarketData/IBrokerClient.cs` | 104 | win | TradingTerminal.Core | B I P | Y | Internal abstraction over a market-data + connection backend (IB, NinjaTrader, ...). |
| `src/windows/Core/TradingTerminal.Core/MarketData/IInstrumentRegistry.cs` | 38 | win | TradingTerminal.Core | B I P | Y | Look up a canonical instrument by id, or null if unknown. |
| `src/windows/Core/TradingTerminal.Core/MarketData/IMarketDataHub.cs` | 27 | win | TradingTerminal.Core | B I P | Y | The live, in-memory, broker-agnostic publish/subscribe bus for normalized market data. |
| `src/windows/Core/TradingTerminal.Core/MarketData/IMarketDataIngest.cs` | 32 | win | TradingTerminal.Core | B I P | Y | Resolve (creating if needed) the canonical id for a broker contract on |
| `src/windows/Core/TradingTerminal.Core/MarketData/IMarketDataRepository.cs` | 65 | win | TradingTerminal.Core | B I P | Y | The single facade for market data. Hides broker SDKs entirely — no |
| `src/windows/Core/TradingTerminal.Core/MarketData/IMarketDataStore.cs` | 85 | win | TradingTerminal.Core | B I P | Y | Queue a quote for batched persistence. Returns immediately. |
| `src/windows/Core/TradingTerminal.Core/MarketData/IQuestDbLauncher.cs` | 24 | win | TradingTerminal.Core | B I P | Y | True only when the QuestDB backend is configured — otherwise there's nothing |
| `src/windows/Core/TradingTerminal.Core/MarketData/Indicators.cs` | 145 | win | TradingTerminal.Core | B I P | Y | EMA: y_t = α·x_t + (1-α)·y_{t-1}, with α = 2 / (period |
| `src/windows/Core/TradingTerminal.Core/MarketData/InstrumentDataView.cs` | 71 | win | TradingTerminal.Core | B I P | Y | The instrument this facade is scoped to. |
| `src/windows/Core/TradingTerminal.Core/MarketData/Microstructure.cs` | 186 | win | TradingTerminal.Core | B I P | Y | Half-spread in price units: |
| `src/windows/Core/TradingTerminal.Core/MarketData/OrderFlowImbalance.cs` | 65 | win | TradingTerminal.Core | B I P | Y | Number of discrete OBI regimes (Section 3.4): nine ordered bins from strongly |
| `src/windows/Core/TradingTerminal.Core/MarketData/Sp100Sp500Catalog.cs` | 328 | win | TradingTerminal.Core | B I P | Y | A single index constituent: its US-stock ticker and company name. |
| `src/windows/Core/TradingTerminal.Core/MarketData/StoredDataExtent.cs` | 24 | win | TradingTerminal.Core | B I P | Y | True when there's at least one row, i.e. both bounds are set. |
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
| `src/windows/Core/TradingTerminal.Core/Strategies/Authoring/AiModelChoice.cs` | 23 | win | TradingTerminal.Core | B I P | Y | False when the provider isn't usable right now (CLI not installed, no |
| `src/windows/Core/TradingTerminal.Core/Strategies/Authoring/IAiKeyResolver.cs` | 38 | win | TradingTerminal.Core | B I P | Y | The API key for |
| `src/windows/Core/TradingTerminal.Core/Strategies/Authoring/IAuthoredStrategyViewComposer.cs` | 24 | win | TradingTerminal.Core | B I P | Y | Composes the default live view for |
| `src/windows/Core/TradingTerminal.Core/Strategies/Authoring/IStrategyCodegenClient.cs` | 221 | win | TradingTerminal.Core | B I P | Y | Who is speaking in a codegen conversation. |
| `src/windows/Core/TradingTerminal.Core/Strategies/Authoring/IStrategyCompiler.cs` | 21 | win | TradingTerminal.Core | B I P | Y | Compiles a user-authored into a runnable |
| `src/windows/Core/TradingTerminal.Core/Strategies/Authoring/StrategyBuildEffort.cs` | 70 | win | TradingTerminal.Core | B I P | Y | Sketch fast: one skill pack, one fix attempt, no review, no smoke. |
| `src/windows/Core/TradingTerminal.Core/Strategies/Authoring/StrategyCompileResult.cs` | 73 | win | TradingTerminal.Core | B I P | Y | True when the author supplied a complete hand-written window: the descriptor, a |
| `src/windows/Core/TradingTerminal.Core/Strategies/Authoring/StrategyDiagnostic.cs` | 36 | win | TradingTerminal.Core | B I P | Y | Severity of a |
| `src/windows/Core/TradingTerminal.Core/Strategies/Authoring/StrategyScript.cs` | 36 | win | TradingTerminal.Core | B I P | Y | The name given to a single unnamed file (the AI didn't label |
| `src/windows/Core/TradingTerminal.Core/Strategies/IStrategyFactory.cs` | 36 | win | TradingTerminal.Core | B I P | Y | Fires when a strategy is added after startup, so a bound catalog |
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
