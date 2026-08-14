# Backtest Studio redesign decisions

- The catalog uses the richer authored `StrategyParameterSchema` as its canonical parameter shape.
  Built-in numeric descriptors project into it and convert back to numeric `RunSpec.Parameters` only
  at the engine boundary. This preserves number, integer, Boolean, choice, and text editors.
- Provenance and execution route are independent. Built-in and current authored managed strategies
  remain order-native; sealed DAXQ registrations use `SignalBacktestRunner`. A future signal artifact
  projection can opt into that route without adding a UI branch.
- `IProtectedStrategyEngine` is decorated inside `AddBacktestStudioSurface` so returned registrations
  retain sealed provenance. Verification-only loads are not listed: a captured id must also exist in
  the running `IBacktestStrategyRegistry`.
- Catalog id collisions preserve the established built-in entry, then sealed DAXQ, then authored and
  Python-authored entries. The sealed id set still prevents the merged registry from adding a second,
  falsely-authored copy.
- Studio timeframe is feed configuration, not a `RunSpec` field. The Pro feed decorator passes raw
  events through and adds causal completed bars whose event timestamp is bucket close and whose bar
  payload retains bucket open. The final partial bucket closes at the last replay timestamp.
- Timeframe-aware Studio runs stay in-process because the frozen worker input has no Studio feed
  configuration seam. This preserves selected-bar behavior without changing a public contract.
- Signal runs use policy version `studio-signal-v1`, the existing conservative one-unit buyer cap, and
  the conservative fixed-contract unit definition. No live execution capability is introduced.
- Optimization and walk-forward remain available for kernel-native projections. They are disabled
  with an explicit status for option-backed/signal entries rather than silently using the wrong route.
- The approved operator rail keeps every former Overview and Settings input, including source-specific
  synthetic, parquet, store, replay, provenance, and schema controls. Export and help actions move into
  the results surface/header rather than being removed.
- KPI comparison lines use only report-backed values and explicit neutral baselines: zero return/P&L,
  50% win rate, and profit factor 1.0. Return is derived from the completed run's starting cash and net
  profit; no prior-run comparison is implied.
- Studio-only typography sets the root to `Font.Base`; `Font.Data` is applied to KPI values, editors,
  dates, prices, replay counters, and result-table cells. No application-wide font resource changes are
  required.
- ScottPlot colors are resolved from the active theme brush resources at render time. This keeps the
  figure, data area, grids, axes, equity fill, candles, and trade markers aligned with TvDark without
  duplicating palette literals in the Studio.
- The Studio owns complete `TabControl` and `TabItem` templates so selected tabs remain transparent with
  an accent underline and cannot fall back to the framework's white selected-tab chrome.
