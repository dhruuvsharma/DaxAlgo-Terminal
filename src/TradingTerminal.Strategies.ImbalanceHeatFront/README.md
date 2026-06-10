# Imbalance Heat Front

`TradingTerminal.Strategies.ImbalanceHeatFront` — live signal window for strategy id **`imbalance.heatfront`**.

> 3D bid/ask imbalance surface over [distance-from-touch × time]. Detects coherent ridges of one-sided book pressure and enters **with** (momentum) or **against** (mean-reversion) the ridge. **Data/signals only — does not place orders.**

## Data requirements

| L1 | Bars | Depth | Trade tape |
|:--:|:--:|:--:|:--:|
| ✅ | ✅ | ✅ | — |

**Requires L2 depth — IB or cTrader.** In the backtester (L1-only) ridge detection degenerates to a single touch-level threshold; the live window uses real depth via `OnDepthAsync` for the full ridge logic.

## How it works

Maintains a rolling matrix of per-distance L2 imbalance ratios over the last `NumSlices` time slices, and looks for a **ridge** — a band of `RidgeWidth`+ consecutive distance levels where `|imbalance| ≥ RidgeThreshold` — that persists for `ConfirmationSlices` consecutive samples on the same side. A confirmed ridge means the book is one-sided across multiple levels, not just at the touch.

Two execution modes:

- **Momentum** — enter with the ridge (long when bids dominate); best when the ridge is growing.
- **MeanReversion** — enter against the ridge on the thesis that one-sided books exhaust and snap back; best when the ridge is shrinking.

Exit on ridge dissolution, sign flip, or TP/SL.

## Parameters (engine defaults)

| Param | Default | Meaning |
|---|--:|---|
| `numLevels` | 5 | depth levels tracked from the touch |
| `numSlices` | 30 | time slices in the rolling matrix |
| `ticksPerSlice` | 20 | ticks aggregated per slice |
| `ridgeThreshold` | 0.75 | \|imbalance\| floor for a ridge cell (0–1) |
| `ridgeWidth` | 3 | consecutive levels needed to form a ridge |
| `confirmationSlices` | 2 | consecutive samples to confirm |
| `mode` | Momentum | `Momentum` or `MeanReversion` |
| `quantity` | 1 | order size |
| `stopLossPips` / `takeProfitPips` | 2 / 4 | exits in raw price units |

## Project layout

| File | Role |
|---|---|
| `ImbalanceHeatFrontStrategy.cs` | `ITradingStrategy` metadata + data-requirement tags |
| `ImbalanceHeatFrontCalculator.cs` | rolling imbalance-matrix / ridge detector |
| `ImbalanceHeatFrontViewModel.cs` | live VM + surface rendering state |
| `ImbalanceHeatFrontWindow.xaml(.cs)` | dashboard view |
| `DependencyInjection.cs` | `AddImbalanceHeatFrontStrategy()` |

## Wiring

- **Engine impl:** `src/TradingTerminal.Infrastructure/Backtest/Strategies/ImbalanceHeatFrontStrategy.cs` (note the L1-only backtest caveat above).
- **Live VM** extends `LiveSignalStrategyViewModelBase`; consumes `IMarketDataHub.Depth(InstrumentId)`.
- **DI:** `services.AddImbalanceHeatFrontStrategy()` from `App.xaml.cs`; opened via `IStrategyFactory`.
