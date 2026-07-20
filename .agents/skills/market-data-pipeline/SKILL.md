---
name: market-data-pipeline
description: Canonical broker-neutral market-data pipeline — InstrumentId/Registry, IMarketDataHub, ref-counted IMarketDataIngest, persistence, normalization, and store backends. Use when adding store tables, changing ingest, wiring broker trade tape, debugging missing/duplicate/mis-timestamped data, or editing src/windows/Pipeline/TradingTerminal.MarketData/.
---

# Market-Data Pipeline

The pipeline replaced the original "broker-shaped types leaking everywhere" model. Every strategy now keys on canonical `InstrumentId`, not broker symbology, and consumes the hub — never the broker directly.

## The four seams

All in `Core/MarketData/`, all implemented in `MarketData/`:

1. **`IInstrumentRegistry`** — canonical identity. Maps broker symbol ↔ `InstrumentId` (auto-creates on first sight). Cached, SQLite-backed. Resolves `Contract` from any broker into the same `InstrumentId`.
2. **`IMarketDataStore`** — durable persistence. **Four interchangeable backends**: `PerBrokerSqliteMarketDataStore` (default — one SQLite file per broker per stream `-bars`/`-l1`/`-trades`/`-l2`, identity in the shared `marketdata.db`; persists L2), single-file `SqliteMarketDataStore`, `NpgsqlMarketDataStore` (Timescale), and `QuestDbMarketDataStore` (+`CompositeMarketDataStore`: L1/L2/trades/depth → QuestDB, bars → SQLite). All over a shared `MarketDataStoreBase` that owns channel + batched writer + flush. **Writes are non-blocking** — `Enqueue*` returns immediately; background batch flushes on `WriteBatchSize` or `FlushIntervalMs`.
3. **`IMarketDataHub`** — Rx per-instrument fanout. Live hot path. Keyed by `InstrumentId`. Three streams: `Quotes(id)`, `Trades(id)`, `Depth(id)`, plus `Bars(id, timeframe)` aggregated downstream.
4. **`IMarketDataIngest`** — bridges broker → hub + store. Ref-counted per-instrument broker subscriptions; normalizes raw broker payloads to canonical records; publishes to hub + persists to store. Depth (L2) is persisted only by the per-broker SQLite (`-l2.db`) and QuestDB backends; the single-file SQLite and Postgres backends treat `EnqueueDepth` as a no-op (live-only on the hub).

Wired via `AddMarketDataPipeline` (in `MarketData/MarketDataPipelineServiceCollectionExtensions.cs`), called from `App.xaml.cs`.

## Canonical records (Core/Domain)

- `Quote(InstrumentId, Bid, BidSize, Ask, AskSize, EventTimeUtc, IngestTimeUtc, Source, Sequence, EventTimeApproximate)`.
- `TradePrint(InstrumentId, Price, Size, Aggressor, EventTimeUtc, IngestTimeUtc, Source, Sequence, EventTimeApproximate)` — note the name: `TradePrint`, not `Trade` (collides with `Core.Backtest.Trade`).
- `OhlcvBar(InstrumentId, Open, High, Low, Close, Volume, Timeframe, EventTimeUtc, IngestTimeUtc, Source, EventTimeApproximate)`.
- `DepthSnapshot(InstrumentId, Bids, Asks, EventTimeUtc, IngestTimeUtc, Source)`.

**Every record carries provenance** — `EventTimeUtc + IngestTimeUtc + Source + EventTimeApproximate`. Never strip these. `EventTimeApproximate=true` means the broker stamped arrival time (`UtcNow`) instead of exchange time (e.g. IB/NT/cTrader for quotes).

## Backends

- **Per-broker SQLite** (`SqlitePerBroker`, the **default**): one file per broker *per stream* — `%LocalAppData%/DaxAlgoTerminal/marketdata-{broker}-bars.db` / `-l1.db` (quotes) / `-trades.db` / `-l2.db` (depth), each created from its own `SqliteSchema.EnsureXCreated` and driven by its own `SqliteMarketDataStore` (a `SqliteStoreStream` value selects the table). Every (broker, stream) writes in parallel, same-instrument bars from different brokers can't collide, and a stream of a broker's history is wiped by deleting one file. **`-l2.db` persists L2 depth** (flattened one row per book level, regrouped on read) with a startup retention prune (`DepthRetentionDays`) — the single-file/Postgres SQLite backends still drop depth. Canonical identity stays in the shared `marketdata.db` registry → `InstrumentId` is broker-neutral. `PerBrokerSqliteMarketDataStore` routes writes by stream + `record.Source`; null-source reads (and all depth reads) k-way-merge ascending across files. No data migration on switch (start-fresh).
- **SQLite** (`Sqlite`, single file): `%LocalAppData%/DaxAlgoTerminal/marketdata.db`, WAL mode, epoch-microseconds timestamps. Everything in one file; same-instrument bars from two brokers overwrite each other (shared `(instrument,size,time)` PK).
- **PostgreSQL + TimescaleDB**: Docker via root `docker-compose.yml` (`timescale/timescaledb:latest-pg16`, db/user/pass=daxalgo, port 5432). `timestamptz` columns, hypertables, retention policy. Free/OSS.
- **Auto-fallback**: If `MarketDataStoreOptions.Provider == Postgres` and the DB is unreachable at startup, the DI factory probes and silently falls back to SQLite. Solo dev shouldn't need Docker just to launch.

- **QuestDB** (split): `Provider: "QuestDb"` routes the high-volume L1/L2 streams (quotes, trades, **depth**) to a QuestDB server while **bars stay in SQLite** — a `CompositeMarketDataStore(tickStore: QuestDbMarketDataStore, barStore: SqliteMarketDataStore)`. Writes use ILP-over-HTTP (`net-questdb-client`, port 9000); reads/DDL use PG-wire via Npgsql (port 8812). Docker service `questdb` in `docker-compose.yml`. **No silent fallback** (unlike Postgres): if QuestDB is unreachable at startup the tick/depth half goes inert (persistence off, logged loudly) and bars keep flowing to SQLite, so the app still launches.

Choose via `appsettings.json`:
```json
{
  "MarketDataStore": {
    "Provider": "SqlitePerBroker",  // default — or "Sqlite" | "Postgres" | "QuestDb"
    "WriteBatchSize": 200,
    "FlushIntervalMs": 250
  }
}
```

## Tick-primary ingest

**Don't write live bars to the store.** As of commit `9757b4d`, bars are aggregated downstream from ticks; the store keeps quotes + trades + (depth live-only, no persistence). If you find code calling `_store.EnqueueBar` for live data, it's wrong.

Historical bars (e.g. warm-up reads) DO go through the store, but they come from the broker's `RequestHistoricalBarsAsync` API and represent closed bars from exchange data — not live aggregation.

## Adding a new store table

1. Migration SQL: add a `CREATE TABLE IF NOT EXISTS` in both `SqliteMarketDataStore.EnsureSchema` and `NpgsqlMarketDataStore.EnsureSchema`. Don't add to only one — they must stay in lockstep.
2. Wire row type: a record in `Core/Domain/` with the provenance fields.
3. `Enqueue<Name>` + `ReadXxxAsync` methods on `IMarketDataStore` (+ both impls). Use the channel/batch infra from `MarketDataStoreBase`.
4. Ingest: a `Pump<Name>Async` in `MarketDataIngestService`, keyed by `InstrumentId`, with the same ref-counting pattern as quotes/trades.
5. Hub: an `Observable<<Name>>` per-instrument fanout in `MarketDataHub`.
6. **Archive**: extend `ArchiveBundleBuilder.Export<Name>Async` + `MarketDataArchiver.Import<Name>Async` so the new table is offloaded too. See [archive-offloader](../archive-offloader/SKILL.md).
7. Postgres retention: if the new table is high-volume, add it to the retention policy in the Postgres init scripts.

## Adding trade tape to a new broker

1. Implement `IBrokerClient.SubscribeTradesAsync` — return `IAsyncEnumerable<TradeTick>` with `[EnumeratorCancellation]`.
2. Stamp the aggressor flag at the source if the broker exposes it (e.g. Alpaca `taker_side`). Otherwise leave `Aggressor=Unknown` and let `MarketDataIngestService.PumpTradesAsync` infer via Lee-Ready (`Microstructure.ClassifyAggressor` against the per-instrument `TradeContext` cache of recent bid/ask).
3. Test against `IMarketDataStore.trades` — confirm rows land with non-null aggressor and provenance.
4. Update focused tests and any current chart/UI capability predicates that enumerate native tape support; do not add or edit an in-tree strategy project.

## Per-instrument facade

`MarketDataRepository` exposes a per-instrument facade over `IMarketDataStore` (commit `cde7fa9`) — UI code can ask for "the last N quotes for InstrumentId X" without thinking about the underlying backend. Use the facade in new UI/strategy code; reserve direct `IMarketDataStore` access for ingest + archive.

## Hard rules

- **Don't subscribe to broker streams from a view-model.** Always go through `IMarketDataIngest.Subscribe(...)`. The handle returned is ref-counted; dispose on Stop.
- **Depth persistence: QuestDB and per-broker SQLite only.** The QuestDB backend and the per-broker SQLite backend's `-l2.db` file persist depth (one row per book level, reconstructed into snapshots on read). The **single-file `Sqlite` and Postgres** stores still deliberately drop depth (`EnqueueDepth` is a no-op there) — too high-volume. `MarketDataIngestService.PumpDepthAsync` always calls `_store.EnqueueDepth(...)`; whether it lands is the backend's call. Don't add a depth table to the single-file SQLite or Postgres schema (`SqliteSchema.EnsureCreated` / `TimescaleSchema`) — only the per-broker `Depth`-stream store (`SqliteSchema.EnsureDepthCreated`) gets one.
- **Don't await disk on the ingest hot path.** `Enqueue*` is fire-and-forget; the batched writer handles flushing.
- **Never strip provenance fields** when projecting canonical records to legacy types. The boundary projection (e.g. `Quote → Tick`) for `OnTickAsync` is the only sanctioned lossy point.
- **`net9.0-windows` target** — Microsoft.Data.Sqlite + Npgsql both work; don't add another ORM.

## Reference reads

- `src/windows/Pipeline/TradingTerminal.MarketData/MarketDataPipelineServiceCollectionExtensions.cs` — DI wiring.
- `src/windows/Pipeline/TradingTerminal.MarketData/MarketDataHub.cs` — Rx fanout.
- `src/windows/Pipeline/TradingTerminal.MarketData/MarketDataIngestService.cs` — ref-counted subscriptions + normalization + persistence.
- `src/windows/Pipeline/TradingTerminal.MarketData/Store/SqliteMarketDataStore.cs` + `NpgsqlMarketDataStore.cs`.
- `src/windows/Pipeline/TradingTerminal.MarketData/InstrumentDiscoveryService.cs`.
- `docs/market-data.md` — user-facing prose.

See also: [archive-offloader](../archive-offloader/SKILL.md) and [add-broker](../add-broker/SKILL.md).
