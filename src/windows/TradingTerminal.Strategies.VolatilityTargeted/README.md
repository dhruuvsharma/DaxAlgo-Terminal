# Volatility Targeting (index)

`TradingTerminal.Strategies.VolatilityTargeted` — live signal window for strategy id **`vol.targeted`**.

> Position = target_vol / realised_vol_ewma. AQR-style risk-parity overlay. **Data/signals only — does not place orders.**

## Data requirements

| L1 | Bars | Depth | Trade tape |
|:--:|:--:|:--:|:--:|
| ✅ | ✅ | — | — |

Universal L1 + Bars baseline — runs on every broker.

## How it works

A volatility-targeted long-bias on a single index. Realised vol is an **EWMA of squared returns**; position size is:

```
size = min(MaxQuantity, round(TargetVol / realised_vol_estimate))
```

When vol spikes (Aug 2015, Feb 2018, Mar 2020 on SPY) exposure shrinks; when vol compresses it scales up — which stabilises max-drawdown materially on index cash. The canonical "vol targeting" overlay behind AQR's risk-parity products and most managed-futures funds (Asness, Moskowitz, Pedersen 2013, *"Value and Momentum Everywhere"*). Long-only here; flip a sign in code to make it symmetric.

## Parameters (engine defaults)

| Param | Default | Meaning |
|---|--:|---|
| `targetVol` | 0.001 | target per-step realised vol |
| `volHalfLife` | 200 | EWMA half-life (ticks) |
| `maxQuantity` | 10 | exposure cap |
| `rebalanceEveryTicks` | 100 | resize cadence |

## Project layout

| File | Role |
|---|---|
| `VolatilityTargetedStrategy.cs` | `ITradingStrategy` metadata + data-requirement tags |
| `VolatilityTargetedStrategyViewModel.cs` | live VM — EWMA vol + sizing state |
| `VolatilityTargetedStrategyWindow.xaml(.cs)` | dashboard view |
| `DependencyInjection.cs` | `AddVolatilityTargetedStrategy()` |

## Wiring

- **Engine impl:** `src/TradingTerminal.Infrastructure/Backtest/Strategies/VolatilityTargetedStrategy.cs`.
- **Live VM** extends `LiveSignalStrategyViewModelBase`; consumes `IMarketDataHub.Quotes(InstrumentId)`.
- **DI:** `services.AddVolatilityTargetedStrategy()` from `App.xaml.cs`; opened via `IStrategyFactory`.
