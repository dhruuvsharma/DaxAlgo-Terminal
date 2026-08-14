# C++ backtester bridge — archived 2026-08-14

The managed half of the polyglot backtest accelerator: the seams and subprocess drivers that shelled
out to `tick_backtester.exe` and `gpu_optimizer.exe`. **Out of the solution, referenced by nothing.**

## Why

The C++ side was never in this repository. `tools/cpp-backtester/` was gitignored local build output
(`/tools/` in the public repo's `.gitignore`, 0 tracked files) and today lives in the sibling
**Tick-BackTester** repo. So the binaries never existed here, which made the whole bridge inert:

- `ProcessFastBacktestRunner.IsAvailable` is `File.Exists(exePath)` — permanently false.
- The DI extension therefore always resolved `NullFastBacktestRunner`.
- The Backtest window's **"Use C++ Fast engine"** checkbox bound `IsEnabled` to that, so it was a
  permanently greyed-out control.
- `HybridGridOptimizer` had **no consumers at all**.

Meanwhile the App csprojs carried three `<None Include>` staging items pointing at
`..\..\..\..\tools\cpp-backtester\...`, plus a `WarnIfCppBacktesterPathStale` target added in
restructure P3 that warned on every build precisely because the anchor was missing. That guard did
its job — this archive is the fix it was asking for.

Retired now rather than later because the backtest engine and studio are being redesigned
(`TradingTerminal.BacktestStudio` archived the same day), and this bridge belongs to the old
architecture.

## What is here

| Archived from | Files |
|---|---|
| `Core/Backtest/Fast/` | `IFastBacktestRunner`, `FastBacktestRequest`, `FastBacktestResult` |
| `Infrastructure/Backtest/Fast/` | `ProcessFastBacktestRunner`, `NullFastBacktestRunner`, `FastBacktestServiceCollectionExtensions` |
| `Backtest.Engine/Optimization/Gpu/` | `ProcessGpuOptimizer`, `HybridGridOptimizer`, `GpuUnavailableException` |

Also removed at the call sites: `AddFastBacktestRunner()` from all three shells, the
`<None Include>` staging blocks and the stale-path guard from the App csprojs, and the
`UseFastEngine` / `IsFastAvailable` surface from `BacktestViewModel` and `BacktestView.xaml`
(including the `UseFastEngine` field on the persisted `BacktestRunPreset` — older preset JSON simply
carries an extra property that is now ignored).

## Restoring it

Move the three directories back, restore `AddFastBacktestRunner()` in each shell's
`Composition/AppDependencyInjection.cs`, and re-add the staging `<None Include>` items. But the
honest advice is **don't** — if a native accelerator is wanted again, design it against the new
backtest engine rather than reviving a subprocess contract built for the old one. The C++ source is
in the Tick-BackTester repo.

History is intact: everything moved with `git mv`, so `git log --follow` still reaches it.
