# Archived tools

Code kept on disk for reference but **out of the solution and referenced by nothing**. It does not
build, is not promoted to any edition, and is not maintained.

## TradingTerminal.Recording — archived 2026-08-22

Set aside to return as a feature in a later update.

**Why.** It was archived as part of settling what the market-data store is *for*. With the backtest
engine gone, the store stopped being an archive and became a recent-window cache — bounded by
retention, deleted on a timer. The recorder's premise was the opposite: capture and keep. Rather
than leave a capture tool wired into a store that now deletes behind it, it comes back with the
design that gives recordings somewhere durable to live.

**What it was.** A header REC chip, a watchlist panel, and `TickRecordingService` — a hosted service
that subscribed the chosen instruments through `IMarketDataIngest` and counted what arrived, plus an
hourly `OffloadPendingAsync` auto-upload to Telegram.

**The thing to know before rebuilding it.** It *recorded nothing of its own*. It started the normal
ingest pumps and incremented counters; every row it produced was written by the ordinary pipeline
into the ordinary tables, with no marker — no session id, no flag. Recorder rows and incidental rows
were the same rows. So "keep what was recorded, expire the rest" could not be expressed at all, and
that is the gap a redesign has to close first: persist a session (instruments, start, stop) and
export its window on stop, rather than hoping the store keeps it.

**One consequence.** Its hourly auto-upload was the second automatic archive trigger. With it gone,
`ArchiveScheduleService` is the only one.

## TradingTerminal.BacktestStudio — archived 2026-08-14

Retired pending a redesign of the backtest engine and studio.

**Why.** The Studio assumes a strategy is one kernel over one instrument's bars, run through
`BacktestEngine` or `SignalBacktestRunner` with a `RunSpec`. The direction of travel is artifacts
that may span multiple instruments, carry several views, and pull external data — which that shape
cannot express, and which cannot be backtested by simply replaying one bar series. Rather than bend
the existing Studio into something it was not designed for, it is set aside so a new engine and
studio can be architected against the real requirement.

**What it was.** The reconciled copy from restructure P5 — Pro's version, verified a strict semantic
superset of Basic's and promoted up on 2026-08-12. It carries `StrategyCatalog` (built-in, authored,
Python-authored and sealed-DAXQ provenance), `BacktestStudioRunner` (order-native vs signal route),
`TimeframeMarketDataFeed`, `ParquetMarketDataFeed`, walk-forward and optimisation rows, and its own
`DECISIONS.md` recording the design calls.

**Restoring it.** `git mv archive/Tools/TradingTerminal.BacktestStudio src/windows/Tools/`, re-add
the project to `TradingTerminal.Windows.slnx`, restore `AddBacktestStudioSurface()` in each shell's
`Composition/AppDependencyInjection.cs`, the `OpenBacktestStudio` command in `MainWindowViewModel`,
and the menu item in `MainWindow.xaml`. Re-add
`src/windows/Tools/TradingTerminal.BacktestStudio/**` to the `base` layer include in the Pro repo's
`tools/daxalgo-sync/layers.json`, then promote.

Its history is intact — it moved with `git mv`, so `git log --follow` still reaches the full record.
