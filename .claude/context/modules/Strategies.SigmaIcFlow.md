# Strategies.SigmaIcFlow — Σ⁻¹·IC Order-Flow Optimizer (flagship)

Shape: see `modules/Strategies-family.md`. **4,390 LOC / 12 files** — the biggest strategy.

- Engine: `Engine/ApexScalperStrategy.cs` (**1,846 LOC — the largest file in the repo**; grep +
  ranged reads only). Tape-primary scalper: Core.Quant estimators → Σ⁻¹·IC weights → isotonic
  g(C) entry → first-passage exits.
- Window/VM: `SigmaIcFlowStrategyWindow.xaml(.cs)` (695/847), `SigmaIcFlowStrategyViewModel.cs` (466).
- First strategy converted to a self-contained runtime plugin (`SigmaIcFlowPlugin`) — the
  template the other 8 followed.
- Needs trade tape (IB/Binance/Ironbeam). Quick backtest: full-tape Binance mode.
- Surface `symbols/Strategies.SigmaIcFlow.md` · Tests: Tests.Headless (kernel referenced directly).
- Math changes: load `quant-math`; follow-ups parked: backtest trade replay, refuse-to-arm toggle.
