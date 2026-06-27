# Index K-Score Surface

`TradingTerminal.Strategies.IndexKScoreSurface` — live signal window for strategy id **`index.kscore.surface`**.

> Multi-stock aggregator for index trading (US30 / S&P 500). Per-component K ∈ [-1.5, +1.5] from 15 indicators × volatility-confidence multiplier; a threshold surface scales inversely with index weight; LONG/SHORT when enough components pierce with cumulative K conviction. **Data/signals only — does not place orders.**

## Data requirements

| L1 | Bars | Depth | Trade tape |
|:--:|:--:|:--:|:--:|
| ✅ | ✅ | — | — |

## How it works

For each index component the strategy computes a **K-score** ∈ [-1.5, +1.5] from 15 indicators, scaled by a per-stock **volatility-confidence multiplier**. A **threshold surface** sets each component's piercing bar inversely to its index weight (heavyweights need less to count). When enough components **pierce** their thresholds and the cumulative K conviction clears the entry bar, the index goes LONG or SHORT.

The **live** window runs the full multi-stock surface (component weights, per-stock thresholds, cross-sectional aggregation). The **engine/backtest** variant is single-instrument — it aggregates one quote stream into fixed-interval bars and computes the same 15-indicator K-score per bar close, entering when `|K|` exceeds `EntryThreshold`. Treat the backtest as a sanity check on the K-score in isolation, not a test of the index-aggregation edge.

## Parameters (engine defaults — single-instrument variant)

| Param | Default | Meaning |
|---|--:|---|
| `barInterval` | 5 min | aggregation bar size |
| `entryThreshold` | 0.40 | `|K|` needed to enter (0–1.5] |
| `exitThreshold` | 0.10 | `|K|` to flatten [0, entry) |
| `quantity` | 1 | order size |

`IndexKScoreParameters` carries the 15-indicator weights.

## Project layout

| File | Role |
|---|---|
| `IndexKScoreSurfaceStrategy.cs` | `ITradingStrategy` metadata + data-requirement tags |
| `IndexComponentCatalog.cs` | index → component-stock definitions |
| `IndexKScoreSurfaceViewModel.cs` | live multi-stock VM + 3D surface state |
| `IndexKScoreSurfaceWindow.xaml(.cs)` | dashboard view |
| `DependencyInjection.cs` | `AddIndexKScoreSurfaceStrategy()` |

## Wiring

- **Engine impl:** `src/TradingTerminal.Infrastructure/Backtest/Strategies/IndexKScoreSurfaceStrategy.cs` (single-instrument). K-score math lives in `Core/IndexKScore` (`IndexKScoreCalculator` / `IndexKScoreParameters`).
- **Live VM** extends `LiveSignalStrategyViewModelBase`; consumes `IMarketDataHub.Quotes/Bars` per component.
- **DI:** `services.AddIndexKScoreSurfaceStrategy()` from `App.xaml.cs`; opened via `IStrategyFactory`.
