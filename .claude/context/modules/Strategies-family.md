# Strategies.* — the shared shape of all 9 live-strategy plugins

Per-strategy docs: `modules/Strategies.<Name>.md`. This file holds what they share — read it once,
not nine times.

**Structure (every strategy).** Own project `src/windows/Strategies/TradingTerminal.Strategies.<Name>/`
referencing ONLY `DaxAlgo.Sdk.Wpf` (ADR-0008). Contains: `<Name>Plugin : IStrategyPlugin` +
`Add<Name>Strategy()` DI ext · `ITradingStrategy` descriptor (Id/pills/BacktestStrategyId) ·
`<Name>StrategyViewModel : LiveSignalStrategyViewModelBase` (ctor = `LiveStrategyHostServices`
only) · `<Name>StrategyWindow` (chrome bar binds by convention; opens maximized, remembers
placement via `OpenStrategy`) · engine kernel (`IBacktestStrategy`) for backtestable ones.

**Rules.** Subscribe via hub only; check `DataRequirement` capability and fail loudly (tape/L2
strategies); bounded channels + coalesced redraw (`memory-safety` — the tape-leak class);
IDisposable teardown; log via `ActivityLog.Append`, no local log panes; strategy-vs-tool rule —
anything with `ITradingStrategy` lives here, never as an `Add…Surface` tool.

**Loading.** Shells discover plugins at runtime via `AddStrategyPlugins()` (shell
`Composition/AppDependencyInjection.cs:105`) → `PluginLoader` — no shell reference to add.

**Change recipe** `RECIPES/add-strategy.md` · **Skills** `add-strategy`, `memory-safety`,
`quant-math` (estimators), `regime-cube-strategy` (cube/surface family).
**Tests** Tests.Headless references CumulativeDelta / FilteredOrderFlow / ImbalanceHeatFront /
SigmaIcFlow kernels directly; others test math via Core.
