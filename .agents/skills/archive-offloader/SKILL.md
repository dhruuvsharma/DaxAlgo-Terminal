---
name: archive-offloader
description: Telegram-backed archive offloader for the canonical market-data store — parquet bundling, 2 GB split-binary parts, sha256 verification, manifest store, retention pruning. Use when touching src/TradingTerminal.MarketData/Archive/, adding store tables to the bundle, changing the archive schedule, swapping the Telegram transport for another backend, or debugging archive/restore round-trips.
---

# Archive Offloader

Stops the local SQLite/Postgres store from growing unboundedly. Hot copy keeps the active window only; everything older is offloaded to the user's Telegram account (forever storage), sha256-verified, and only then pruned locally.

## Architecture

Core seams: `Core/MarketData/Archive/` — `IArchiveTransport`, `IMarketDataArchiver`.

Market-data project (`TradingTerminal.MarketData`): `Archive/` —

- **`ArchiveBundleBuilder`** — exports one parquet file per `(instrument, table)` for a date range. Writes `manifest.json` (instruments, ranges, row counts, sha256s). Zips the bundle, then binary-splits the zip at ~1.9 GB per part (Telegram MTProto caps at 2 GB; we leave headroom).
- **`MarketDataArchiver`** — orchestrator. `Build bundle → upload via IArchiveTransport → re-download + sha256 verify each part → record manifest row → delete local rows`. Round-trip verify is non-negotiable — verify, *then* prune.
- **`ArchiveManifestStore`** — SQLite at `%LocalAppData%/DaxAlgoTerminal/archive-manifest.db`, independent of the main store backend (so the manifest survives even if the user nukes the main store).
- **`TelegramArchiveTransport`** — `WTelegramClient` 4.4.4 (NuGet). User-account MTProto (not bot), 2 GB per file. Session at `%LocalAppData%/DaxAlgoTerminal/telegram-session.bin`. Default destination = Saved Messages; user-supplied username/channel ref also works.
- **`ArchiveScheduleService`** — `IHostedService`, ticks every 15 min, runs the closed-period archive at the configured UTC hour (default 03:00). Idempotent — same range → no-op.

UI: `App/Archive/`

- `ArchiveSettingsView` — Telegram creds + login button + period (weekly/monthly) + tables to include + retention knobs + manual offload range picker.
- `ArchiveActivityView` — DataGrid of past archives + Restore button.
- `WpfTelegramAuthPrompt` — modal dialog bridging WTelegramClient's sync `Config` callback to the async UI for verification-code / 2FA password input.

## Adding a new store table to the bundle

When [market-data-pipeline](../market-data-pipeline/SKILL.md) gains a new table:

1. **Parquet wire row type** — new record in `ArchiveRecords.cs`.
2. **Export** — add `ExportXxxAsync` to `ArchiveBundleBuilder` mirroring `ExportQuotesAsync` / `ExportTradesAsync`.
3. **Import** — add `ImportXxxAsync` to `MarketDataArchiver` for the restore path. Schema must match `IMarketDataStore.EnqueueXxx`.
4. **Manifest** — add the table to `manifest.json` row counts so verify covers it.
5. **Retention** — if the new table is high-volume, add it to the Postgres retention policy AND ensure archive period < retention window (otherwise data is pruned before being offloaded — silent loss).

## Hard rules

- **Verify, then prune.** Never delete local rows before sha256-verifying the re-downloaded parts. The default for "verify failed" is "keep local + log loud", never "delete anyway".
- **Telegram session is per-user-per-machine.** Don't commit `telegram-session.bin`. Don't ship it. It's a credential.
- **Manifest store backend is independent.** Keep it SQLite even if the main store is Postgres — the manifest must outlive backend swaps.
- **No retention < archive period.** If retention < archive period, data hits the prune cutoff before the next archive run can ship it. Validate in `ArchiveScheduleService`.
- **Idempotent runs.** Running the schedule twice on the same range must be a no-op (check manifest for existing row).
- **Depth archiving is QuestDB-only and opt-in.** Depth is only *persisted* on the QuestDB backend (see [market-data-pipeline](../market-data-pipeline/SKILL.md)), so `ArchiveTables.Depth` only produces files there; on SQLite/Postgres `ReadDepthAsync` is empty and the export is a no-op. Off by default (`IncludeDepth=false`) — depth multiplies storage 10–100×. Fidelity caveat: `DepthSnapshot` carries no source/ingest-time in-band, so archived depth round-trips book structure + event time only (restore stamps `Source` from the stored value, default 0).
- **QuestDB prune = partition drop, range-bounded.** QuestDB has no row-level DELETE. The store's `Delete*InRangeAsync` map to `ALTER TABLE … DROP PARTITION WHERE ts >= from AND ts < to`, which only drops partitions *fully inside* the range — boundary partitions are left intact, so it never deletes older un-archived data. Row counts return -1 (unknown); the prune message prints "partition(s)".

## Reference reads

- `src/TradingTerminal.MarketData/Archive/ArchiveBundleBuilder.cs`
- `src/TradingTerminal.MarketData/Archive/MarketDataArchiver.cs`
- `src/TradingTerminal.MarketData/Archive/Telegram/TelegramArchiveTransport.cs`
- `src/TradingTerminal.MarketData/Archive/ArchiveScheduleService.cs`
- `src/TradingTerminal.App/Archive/` — settings + activity views.

See also: [market-data-pipeline](../market-data-pipeline/SKILL.md), [[project-market-data-archive]] (memory).
