# ADR-0005 — Per-broker, per-stream SQLite as the default store

**Date** 2026-06-18 · **Status** accepted (default `SqlitePerBroker`)

**Context.** A single SQLite file serialized concurrent broker writers and let one broker's
data quality pollute another's; L2 depth wasn't persisted at all.

**Decision.** One file per broker PER STREAM: `marketdata-{broker}-bars|l1|trades|l2.db`
(the `-l2` file persists depth, which other SQLite backends drop). Canonical identity stays in
the shared `marketdata.db` registry. Alternatives remain: single-file Sqlite, Postgres+Timescale
(auto-falls back to SQLite), QuestDB split (no silent fallback). Store reads take optional
`BrokerKind? source` (null = merged).

**Consequences.** Parallel writers, broker isolation, no bar collisions. No migration path —
started fresh. New stream kinds need a new per-broker file naming decision.
