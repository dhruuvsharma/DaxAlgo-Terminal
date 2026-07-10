# TradingTerminal.MarketData — canonical pipeline

**Path** `src/windows/Pipeline/TradingTerminal.MarketData/` · 5,825 LOC / 38 files · **Editions** B I P · **Blast: HIGH**

**Purpose.** Broker-neutral pipeline: hub (Rx fanout) · ingest (ref-counted pumps, normalization) ·
repository (UI marshal via `IUiDispatcher` — the ONLY marshal point) · store backends · archive ·
instrument registry. Sits BELOW Infrastructure (never reference it).

**Entry types.** `MarketDataRepository`, `MarketDataPipelineServiceCollectionExtensions.AddMarketDataPipeline(cfg):25`,
store impls (`SqliteMarketDataStore` 435 LOC, per-broker default, Postgres, QuestDB), `MarketDataArchiver`
(574 LOC, Telegram offload), `FeedChannel`/`FeedDropMeter` (drop diagnostics), `LocalParquetLakeExporter`.
Surface: `symbols/MarketData.md` (463 lines). Seams it implements: `symbols/Core-MarketData.md`.

**Depends on** Core only. **Depended by** Infrastructure, Backtest.Engine, BacktestStudio, both shells, tests.

**Invariants.** Tick-primary (ADR-0004); non-blocking `Enqueue*`; per-broker per-stream files
(ADR-0005); provenance preserved; reads take `BrokerKind? source` (null = merged).

**Tests** `tests/TradingTerminal.Tests.Headless` `~MarketData` / `~Store`; Postgres self-skips without Docker.

**Common changes.** Follow `RECIPES/market-data-pipeline-change.md` (store change = ALL backends +
archive bundle). Load `market-data-pipeline` skill. Usually cross-tree.
