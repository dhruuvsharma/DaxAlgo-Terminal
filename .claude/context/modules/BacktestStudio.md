# TradingTerminal.BacktestStudio — studio over the engine rewrite

**Path** `src/windows/Tools/TradingTerminal.BacktestStudio/` · **Editions** B I P · **Blast: med (leaf window)**

**Purpose.** The richer backtest workbench over `TradingTerminal.Backtest.Engine` (the rewrite):
runs, sweeps, comparisons; also the landing surface for paper-tagged reproduced strategies
(`IReproStrategyRegistrar` → Backtest Studio + confidence, per the Paper Lab pipeline).

**Depends on** Backtest.Engine, Core, Infrastructure, MarketData, UI, UI.Core (the only tool that
references the engine + MarketData directly). **Surface** `symbols/BacktestStudio.md`
(`BacktestStudioViewModel.cs` ).

**Tests** Tests.Headless `~BacktestStudio`. **Common changes.** New run configuration, result
visualizations, repro-strategy integration. Load `backtest-engine`; Paper-Lab-touching work loads
`paper-reproduction` (and `untrusted-execution` if the sandbox is involved).
