# TradingTerminal.OrderBook — DOM window + micro-ML forecaster

**Path** `src/windows/Charts/TradingTerminal.OrderBook/` · **Editions** B I P · **Blast: med (leaf window)**

**Purpose.** Depth-of-market ladder with the online-RLS micro-forecaster (microprice path
250ms–5s + spread/depth/sweep event probabilities vs queue-imbalance baseline; violet gutter
path + chips; warm-start from stored L2 via `DepthStepSampler`).

**Key files.** `OrderBookViewModel.cs` (grep + ranged reads only),
`OrderBookWindow.xaml(.cs)` (485/415). ML math: `Core/Ml/OrderBookMicroPredictor.cs` (527) —
surface in `symbols/Core-Ml.md`. **DI** `Add…Surface` ext in project. **Surface** `symbols/OrderBook.md`.

**Depends on** Core, Infrastructure, UI, UI.Core.

**Invariants.** Depth via hub only; bounded channels, batch-drain, coalesced render (this family
caused the 20 GB leak class); predictor state disposed on close. DevSim visual check for chart-ML
windows still pending (memory: project_orderbook_ml).

**Tests** Tests.Headless `~OrderBook` / `~MicroPredictor`. **Common changes.** Predictor features,
gutter rendering, warm-start paths. Load `memory-safety`; math changes also `quant-math`.
