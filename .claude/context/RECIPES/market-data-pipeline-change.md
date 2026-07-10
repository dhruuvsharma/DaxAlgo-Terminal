# RECIPE — canonical-pipeline change (pairs with the `market-data-pipeline` skill)

Fan-out order (top-down, so each layer compiles against the one below):
1. **Core types** — `Core/MarketData/` (events carry provenance: EventTimeUtc, IngestTimeUtc,
   Source, Sequence, EventTimeApproximate — never strip; identity is `InstrumentId`).
2. **Store** — a schema/read change hits ALL backends in `MarketData/Store/`: SqlitePerBroker
   (default; per-broker per-stream files), single-file Sqlite, Postgres/Timescale (SQLite
   fallback), QuestDB split. Writes stay non-blocking `Enqueue*`; live bars are NEVER stored (ADR-0004).
3. **Ingest / Hub / Repository** — `MarketData/` (ref-counted `Subscribe*`; UI marshal happens in
   `MarketDataRepository` via `IUiDispatcher`, nowhere else).
4. **Archive** — new stored streams must join the Telegram bundle (`MarketData/Archive/`,
   `archive-offloader` skill) or be explicitly excluded.
5. **Brokers** — only if capability surface changed (`IBrokerClient`, then each
   `Infrastructure/<Broker>/Real*Client.cs` — 7 files 480–760 LOC; grep, ranged reads).
6. **Consumers** — hub subscribers (strategy base, chart VMs) only for new stream kinds.

Signatures first: `symbols.md` hot-seam digest + `symbols/Core-MarketData.md` + `symbols/MarketData.md`.
Build: `dotnet build TradingTerminal.Windows.slnx` (cross-cutting). Test:
`--filter "FullyQualifiedName~MarketData"` (+ `~Store`); Postgres tests self-skip without Docker.
Usually a cross-tree fix — see `cross-tree-fix.md`. Update: `symbols/` regen, `docs/market-data.md`, issue tick.
