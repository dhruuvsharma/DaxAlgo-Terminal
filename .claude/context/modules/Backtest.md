# TradingTerminal.Backtest — Tools→Backtest window + Quick backtest

**Path** `src/windows/Tools/TradingTerminal.Backtest/` · **Editions** B I P · **Blast: med (leaf window)**

**Purpose.** The Backtest window over the legacy tick engine (`Infrastructure/Backtest/`) plus the
strategy-catalog right-click **Quick backtest** (`QuickBacktestViewModel.cs` ): Binance
full-tape mode (real aggTrades → synth L1) or bar-synthetic; maps `ITradingStrategy.
BacktestStrategyId` → engine strategy. Depth/OBI can't be backtested (no historical L2).

**Depends on** Core, Infrastructure, UI, UI.Core. **Surface** `symbols/Backtest.md`.
**Tests** Tests.Headless `~Backtest` (window-level; engine math tests live with the engine).

**Common changes.** New engine strategy exposure (catalog registration in
`Infrastructure/Backtest/BacktestStrategyCatalog.cs`), result stats display, tape-source options.
Load `backtest-engine` skill; engine changes follow `RECIPES/backtest-engine-change.md`.
