# Order Flow Surface Spike

`TradingTerminal.Strategies.OrderFlowSurfaceSpike` — live signal window for strategy id **`orderflow.surface.spike`**.

> 3D Z-score surface over a rolling [time slice × price bin] matrix of signed order flow. Enters in the direction of a confirmed spike in the latest slice; exits on TP/SL or Z reversion. **Data/signals only — does not place orders.**

Part of the regime-cube/surface family (see `ideas.md`), with Helix Toolkit 3D surface viz.

## Data requirements

| L1 | Bars | Depth | Trade tape |
|:--:|:--:|:--:|:--:|
| ✅ | ✅ | — | ✅ |

**Requires the trade tape** for true signed flow — IB only; other brokers throw `NotSupportedException`. A `QuoteDerivedTradeSynthesizer` provides a fallback where no tape is available.

## How it works

Maintains a rolling matrix `[NumSlices time slices × price bins]` of signed trade volume (buy − sell), Z-score-normalizes the **whole surface** every tick, and looks for spikes in the **latest** slice — any cell whose `|Z|` breaches `SpikeThreshold`. The intuition: an isolated cell standing far above its neighbours in space and time is informed flow concentrating at a price level.

Enter in the spike's direction (positive Z ⇒ buy pressure ⇒ long) after `ConfirmationTicks` consecutive same-direction breaches. Exit on fixed TP/SL **or** immediately when the spike dissipates (Z drops below threshold or flips against the position).

Bins are absolute price levels keyed by `floor(price / PriceBinSize)` in a sparse per-slice dictionary — no re-centering. "Pips" in the params is a misnomer: values are **raw price units** (0.0001 EURUSD, 0.25 ES, 1.0 BTC, …).

## Parameters (engine defaults)

| Param | Default | Meaning |
|---|--:|---|
| `ticksPerSlice` | 100 | ticks aggregated per time slice |
| `numSlices` | 30 | slices in the rolling surface |
| `priceBinSize` | 0.05 | price-bin width (raw units) |
| `spikeThreshold` | 2.5 | `|Z|` to flag a spike |
| `confirmationTicks` | 2 | consecutive breaches to confirm |
| `stopLossPips` / `takeProfitPips` | 20 / 40 | exits in raw price units |
| `quantity` | 1 | order size |

## Project layout

| File | Role |
|---|---|
| `OrderFlowSurfaceSpikeStrategy.cs` | `ITradingStrategy` metadata + data-requirement tags |
| `OrderFlowSurfaceCalculator.cs` | rolling surface + Z-score / spike detection |
| `QuoteDerivedTradeSynthesizer.cs` | synthesizes trades from quotes where no tape is available |
| `OrderFlowSurfaceSpikeViewModel.cs` | live VM + Helix 3D surface state |
| `OrderFlowSurfaceSpikeWindow.xaml(.cs)` | dashboard view |
| `DependencyInjection.cs` | `AddOrderFlowSurfaceSpikeStrategy()` |

## Wiring

- **Engine impl:** `src/TradingTerminal.Infrastructure/Backtest/Strategies/OrderFlowSurfaceSpikeStrategy.cs`.
- **Live VM** extends `LiveSignalStrategyViewModelBase`; checks trade-tape capability before subscribing.
- **DI:** `services.AddOrderFlowSurfaceSpikeStrategy()` from `App.xaml.cs`; opened via `IStrategyFactory`.
