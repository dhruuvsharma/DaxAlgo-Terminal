# Order-Flow Toxicity / VPIN (L1 approx.)

`TradingTerminal.Strategies.OrderFlowToxicity` — live signal window for strategy id **`order.flow.toxicity`**.

> VPIN-style `|Σ signed| / Σ|signed|`. Mean-revert against high toxicity (Easley–López de Prado–O'Hara). **Data/signals only — does not place orders.**

## Data requirements

| L1 | Bars | Depth | Trade tape |
|:--:|:--:|:--:|:--:|
| ✅ | ✅ | — | — |

> **Naming note:** despite the "VPIN" name, the shipped engine is the **L1 tick-rule approximation**, so it needs only the universal L1 + Bars baseline and runs on every broker. A *true* VPIN (trade-by-trade classification + filled volumes) would require the **trade tape** — that's a future upgrade, not the current tag.

## How it works

VPIN (Easley, López de Prado & O'Hara 2012, *"Flow Toxicity and Liquidity in a High-Frequency World"*) buckets trades by volume, classifies each bucket buy/sell-initiated, and calls the running `|buy − sell| / total` the **toxicity**. High toxicity ⇒ informed flow is present ⇒ market makers widen quotes and the next move tends to be against the prevailing aggressor.

**L1 fallback used here:** when a tick's mid ticks up, the aggregate L1 size is classified buy-initiated; down ⇒ sell-initiated. Toxicity is the rolling `|buy − sell| / total` over `WindowTicks`. When it exceeds `ToxicityThreshold`, **enter counter** to the prevailing imbalance (mean-reversion bet that the market overshoots on toxic flow).

## Parameters (engine defaults)

| Param | Default | Meaning |
|---|--:|---|
| `windowTicks` | 200 | rolling toxicity window |
| `toxicityThreshold` | 0.55 | toxicity floor to trade |
| `holdTicks` | 100 | time-stop in ticks |
| `quantity` | 1 | order size |

## Project layout

| File | Role |
|---|---|
| `OrderFlowToxicityStrategy.cs` | `ITradingStrategy` metadata + data-requirement tags |
| `OrderFlowToxicityStrategyViewModel.cs` | live VM — rolling toxicity + signal state |
| `OrderFlowToxicityStrategyWindow.xaml(.cs)` | dashboard view |
| `DependencyInjection.cs` | `AddOrderFlowToxicityStrategy()` |

## Wiring

- **Engine impl:** `src/TradingTerminal.Infrastructure/Backtest/Strategies/OrderFlowToxicityStrategy.cs`.
- **Live VM** extends `LiveSignalStrategyViewModelBase`; consumes `IMarketDataHub.Quotes(InstrumentId)`.
- **DI:** `services.AddOrderFlowToxicityStrategy()` from `App.xaml.cs`; opened via `IStrategyFactory`.
