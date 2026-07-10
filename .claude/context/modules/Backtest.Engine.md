# TradingTerminal.Backtest.Engine — MT5-grade engine rewrite

**Path** `src/windows/Backtest/TradingTerminal.Backtest.Engine/` · 1,950 LOC / 31 files · **Editions** B I P · **Blast: med**

**Purpose.** The ground-up portfolio backtester (P0–P6 phases; P0 contracts in
`Core/Backtesting/`). Coexists with the legacy tick engine in `Infrastructure/Backtest/` —
see `RECIPES/backtest-engine-change.md` for which is which.

**Depends on** Core, MarketData. **Depended by** BacktestStudio, both test projects.
**Surface** `symbols/Backtest.Engine.md` (250 lines).

**Invariants.** Deterministic given the same tape; fee/risk models pluggable; no UI types.
Headless CLI consumer moved to the private Pro repo (Windows copy) — flag CLI-affecting changes.

**Tests** Tests.Headless `~Backtest` (numeric assertions required for fills/fees).
**Common changes.** New statistic, fill-model refinement, walk-forward seam (pluggable per the
plugin-marketplace plan). Load `backtest-engine` skill first.
