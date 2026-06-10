# APEX Microstructure Scalper

`TradingTerminal.Strategies.ApexScalper` — live signal window for strategy id **`apex.scalper`**.

> Weighted composite of 8 order-flow signals (Delta, VPIN, OBI shallow/deep, Footprint, Absorption, HVP, Tape Speed) with regime-adaptive weights, a conflict filter, and dynamic HVP-anchored stops. **Data/signals only — does not place orders.**

## Data requirements

| L1 | Bars | Depth | Trade tape |
|:--:|:--:|:--:|:--:|
| ✅ | ✅ | ✅ | — |

L2 depth is required for the OBI (order-book imbalance) and absorption signals — IB or cTrader.

## How it works

Port of the MT5 ApexScalper EA. On each tick it aggregates an internal order-flow candle and scores 8 sub-signals:

- **Delta** — signed (buy − sell) volume within the candle.
- **VPIN** — volume-synchronised toxicity proxy.
- **OBI shallow / deep** — order-book imbalance at the touch and across deeper levels.
- **Footprint** — per-price buy/sell clustering.
- **Absorption** — large resting size eating aggressive flow without moving price.
- **HVP** — high-volume price node proximity.
- **Tape Speed** — trade arrival rate.

Each score is a `(Score, Confidence, Direction, IsValid)` tuple. They combine into a **weighted composite** whose weights adapt to the detected regime. A **conflict filter** suppresses entries when signals disagree, and a confirmation gate layers session, spread, daily-loss and cooldown checks. Stops are anchored behind the nearest **HVP** node; a kill-switch flattens on daily-loss breach.

## Project layout

| File | Role |
|---|---|
| `ApexScalperStrategy.cs` | `ITradingStrategy` metadata (id, display name, data-requirement tags) |
| `ApexScalperStrategyViewModel.cs` | Live VM — subscribes to the hub, renders composite + per-signal scores |
| `ApexScalperStrategyWindow.xaml(.cs)` | Dashboard view |
| `DependencyInjection.cs` | `AddApexScalperStrategy()` — registers VM, window, and `StrategyFactoryRegistration` |

## Wiring

- **Engine impl:** `src/TradingTerminal.Infrastructure/Backtest/Strategies/ApexScalperStrategy.cs` (the `IBacktestStrategy` with the live scoring logic + UI snapshot records).
- **Live VM** extends `LiveSignalStrategyViewModelBase` (in `TradingTerminal.UI`) and consumes `IMarketDataHub.Quotes/Bars/Depth(InstrumentId)`.
- **DI:** `services.AddApexScalperStrategy()` from `App.xaml.cs`. Opened via `IStrategyFactory` — the shell never references the concrete type.
