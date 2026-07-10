# RECIPE — backtest-engine change (pairs with the `backtest-engine` skill)

Two engine sites (don't confuse):
- **Legacy/live seam**: `Core/Backtest/` (`IBacktestStrategy.cs:16`, IOrderRouter, IClock) with
  engine internals in `Infrastructure/Backtest/` (SimulatedOrderBook, L1FillModel, IFeeModel
  Zero/MakerTaker/Bps, IRiskManager, BacktestSession, strategy catalog @
  `Infrastructure/Backtest/BacktestStrategyCatalog.cs`).
- **Rewrite (P0 contracts)**: `Core/Backtesting/` + `Backtest/TradingTerminal.Backtest.Engine/`
  (portfolio, MT5-grade; phased P0–P6).

Checklist:
1. Signatures first: `symbols/Core-Backtest.md`, `symbols/Core-Backtesting.md`,
   `symbols/Infrastructure-Backtest.md`, `symbols/Backtest.Engine.md`.
2. Strategy-facing seam change (`IBacktestStrategy`, order/fill/fee/risk records) ⇒ every engine
   strategy + every plugin's engine kernel recompiles — check `deps.json` blast radius first.
3. UI consumers: Tools→Backtest window (`TradingTerminal.Backtest`, incl. Quick backtest) and
   `TradingTerminal.BacktestStudio`.
4. The headless CLI's Windows copy lives in the PRIVATE Pro repo — flag CLI impact for Dhruv;
   the Linux tree still carries its own CLI copy.
5. Fees/fills/risk changes need numeric tests: `--filter "FullyQualifiedName~Backtest"`.

Build: `dotnet build TradingTerminal.Windows.Intermediate.slnf` (slnx if Core seams moved).
Usually cross-tree (`cross-tree-fix.md`). Update: `docs/backtesting.md`, `symbols/` regen, issue tick.
