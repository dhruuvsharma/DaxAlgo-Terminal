# Archived tools

Code kept on disk for reference but **out of the solution and referenced by nothing**. It does not
build, is not promoted to any edition, and is not maintained.

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
