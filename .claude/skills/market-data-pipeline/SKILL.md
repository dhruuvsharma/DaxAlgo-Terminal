---
name: market-data-pipeline
description: Canonical broker-neutral market-data pipeline — InstrumentId / InstrumentRegistry, IMarketDataHub (Rx fanout), IMarketDataIngest (ref-counted subscriptions, normalization, persistence), IMarketDataStore (SQLite default, Postgres/TimescaleDB optional, batched non-blocking writes). Use when adding store tables, changing ingest normalization, wiring trade-tape into a new broker, debugging "no data" / "duplicate ticks" / "wrong timestamps", or touching anything under src/TradingTerminal.MarketData/.
---

# Market-Data Pipeline

The pipeline replaced the original "broker-shaped types leaking everywhere" model. Every strategy now keys on canonical `InstrumentId`, not broker symbology, and consumes the hub — never the broker directly.

## The four seams

All in `Core/MarketData/`, all implemented in `MarketData/`:

1. **`IInstrumentRegistry`** — canonical identity. Maps broker symbol ↔ `InstrumentId` (auto-creates on first sight). Cached, SQLite-backed. Resolves `Contract` from any broker into the same `InstrumentId`.
2. **`IMarketDataStore`** — durable persistence. Two interchangeable backends (`SqliteMarketDataStore` / `NpgsqlMarketDataStore`) over a shared `MarketDataStoreBase` that owns channel + batched writer + flush. **Writes are non-blocking** — `Enqueue*` returns immediately; background batch flushes on `WriteBatchSize` or `FlushIntervalMs`.
3. **`IMarketDataHub`** — Rx per-instrument fanout. Live hot path. Keyed by `InstrumentId`. Three streams: `Quotes(id)`, `Trades(id)`, `Depth(id)`, plus `Bars(id, timeframe)` aggregated downstream.
4. **`IMarketDataIngest`** — bridges broker → hub + store. Ref-counted per-instrument broker subscriptions; normalizes raw broker payloads to canonical records; publishes to hub + persists to store (depth is live-only — not persisted).

Wired via `AddMarketDataPipeline` (in `MarketData/MarketDataPipelineServiceCollectionExtensions.cs`), called from `App.xaml.cs`.

## Canonical records (Core/Domain)

- `Quote(InstrumentId, Bid, BidSize, Ask, AskSize, EventTimeUtc, IngestTimeUtc, Source, Sequence, EventTimeApproximate)`.
- `TradePrint(InstrumentId, Price, Size, Aggressor, EventTimeUtc, IngestTimeUtc, Source, Sequence, EventTimeApproximate)` — note the name: `TradePrint`, not `Trade` (collides with `Core.Backtest.Trade`).
- `OhlcvBar(InstrumentId, Open, High, Low, Close, Volume, Timeframe, EventTimeUtc, IngestTimeUtc, Source, EventTimeApproximate)`.
- `DepthSnapshot(InstrumentId, Bids, Asks, EventTimeUtc, IngestTimeUtc, Source)`.

**Every record carries provenance** — `EventTimeUtc + IngestTimeUtc + Source + EventTimeApproximate`. Never strip these. `EventTimeApproximate=true` means the broker stamped arrival time (`UtcNow`) instead of exchange time (e.g. IB/NT/cTrader for quotes).

## Backends

- **SQLite** (default): `%LocalAppData%/DaxAlgoTerminal/marketdata.db`, WAL mode, epoch-microseconds timestamps. Zero-config — works out of the box.
- **PostgreSQL + TimescaleDB**: Docker via root `docker-compose.yml` (`timescale/timescaledb:latest-pg16`, db/user/pass=daxalgo, port 5432). `timestamptz` columns, hypertables, retention policy. Free/OSS.
- **Auto-fallback**: If `MarketDataStoreOptions.Provider == Postgres` and the DB is unreachable at startup, the DI factory probes and silently falls back to SQLite. Solo dev shouldn't need Docker just to launch.

Choose via `appsettings.json`:
```json
{
  "MarketDataStore": {
    "Provider": "Sqlite",    // or "Postgres"
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
4. Update the capability matrix in [[project-strategy-ideas]] (memory) and [regime-cube-strategy](../regime-cube-strategy/SKILL.md) — flip the broker from `false` to `true` in `BrokerSupportsTradeTape`.

## Per-instrument facade

`MarketDataRepository` exposes a per-instrument facade over `IMarketDataStore` (commit `cde7fa9`) — UI code can ask for "the last N quotes for InstrumentId X" without thinking about the underlying backend. Use the facade in new UI/strategy code; reserve direct `IMarketDataStore` access for ingest + archive.

## Hard rules

- **Don't subscribe to broker streams from a view-model.** Always go through `IMarketDataIngest.Subscribe(...)`. The handle returned is ref-counted; dispose on Stop.
- **Don't persist depth.** Depth is live-only by design. If a tab needs a historical depth snapshot, it's wrong — depth is too high-volume.
- **Don't await disk on the ingest hot path.** `Enqueue*` is fire-and-forget; the batched writer handles flushing.
- **Never strip provenance fields** when projecting canonical records to legacy types. The boundary projection (e.g. `Quote → Tick`) for `OnTickAsync` is the only sanctioned lossy point.
- **`net9.0-windows` target** — Microsoft.Data.Sqlite + Npgsql both work; don't add another ORM.

## Reference reads

- `src/TradingTerminal.MarketData/MarketDataPipelineServiceCollectionExtensions.cs` — DI wiring.
- `src/TradingTerminal.MarketData/MarketDataHub.cs` — Rx fanout.
- `src/TradingTerminal.MarketData/MarketDataIngestService.cs` — ref-counted subscriptions + normalization + persistence.
- `src/TradingTerminal.MarketData/Store/SqliteMarketDataStore.cs` + `NpgsqlMarketDataStore.cs`.
- `src/TradingTerminal.MarketData/InstrumentDiscoveryService.cs`.
- `docs/market-data.md` — user-facing prose.

See also: [archive-offloader](../archive-offloader/SKILL.md), [add-broker](../add-broker/SKILL.md), [regime-cube-strategy](../regime-cube-strategy/SKILL.md).
