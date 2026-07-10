# ADR-0004 — Tick-primary ingest; live bars are never persisted

**Status** accepted

**Context.** Persisting broker-pushed live bars alongside ticks caused duplicate/conflicting
history across brokers and made the store's truth ambiguous.

**Decision.** Ingest persists ticks/quotes/trades/depth only. Bars are aggregated downstream
from ticks; `IMarketDataStore.EnqueueBar` is for backfill/historical paths, not live streams.
Store writes are non-blocking (`Enqueue*` + batched background writer).

**Consequences.** Never write live bars to the store from a VM, pump, or strategy. Bar queries
served from stored bars (historical) or aggregation. Provenance fields
(`EventTimeUtc/IngestTimeUtc/Source/Sequence/EventTimeApproximate`) are never stripped, so
merged reads stay attributable. `verify-on-stop` + review watch for violations.
