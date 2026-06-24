# Ornstein–Uhlenbeck Mean Reversion

`TradingTerminal.Strategies.OrnsteinUhlenbeck` — live signal window for strategy id **`ornstein.uhlenbeck`**.

> OLS-fit AR(1)-as-OU on the rolling price window; trade z-score deviations with separate entry / exit / stop bands. **Data/signals only — does not place orders.**

## Data requirements

| L1 | Bars | Depth | Trade tape |
|:--:|:--:|:--:|:--:|
| ✅ | ✅ | — | — |

Universal L1 + Bars baseline — runs on every broker.

## How it works

Models the mid price as an Ornstein–Uhlenbeck process `dX_t = θ(μ − X_t) dt + σ dW_t`, discretised as AR(1):

```
X_{t+1} = a + b·X_t + ε,   b = e^{−θΔt},   a = μ(1−b),   Var(ε) = σ²(1−b²)/(2θ)
```

Parameters are estimated **online** by rolling-window OLS over the last `Lookback` mids, refit every `RefitEvery` ticks. It trades the z-score of the current mid against the OU stationary distribution:

```
z = (X_t − μ̂) / σ̂_stat,   σ̂_stat² = σ² / (2θ)
```

- Enter **long** when `z ≤ −EntryZ`; **short** when `z ≥ EntryZ`.
- Flatten when `|z| ≤ ExitZ`.
- Stop out at `|z| ≥ StopZ` (the process appears to have broken — regime shift / news).

A principled cousin of the demo `MeanReversionStrategy`; useful as a sanity probe on real intraday data where prices are often locally OU. See the `quant-math` skill for the calibration derivation.

## Parameters (engine defaults)

| Param | Default | Meaning |
|---|--:|---|
| `lookback` | 500 | OLS rolling window (ticks) |
| `refitEvery` | 50 | re-estimate cadence (ticks) |
| `entryZ` | 2.0 | entry band |
| `exitZ` | 0.25 | flatten band |
| `stopZ` | 4.0 | stop band |
| `quantity` | 1 | order size |

## Project layout

| File | Role |
|---|---|
| `OrnsteinUhlenbeckStrategy.cs` | `ITradingStrategy` metadata + data-requirement tags |
| `OrnsteinUhlenbeckStrategyViewModel.cs` | live VM — online OU fit + z-score state |
| `OrnsteinUhlenbeckStrategyWindow.xaml(.cs)` | dashboard view |
| `DependencyInjection.cs` | `AddOrnsteinUhlenbeckStrategy()` |

## Wiring

- **Engine impl:** `src/TradingTerminal.Infrastructure/Backtest/Strategies/OrnsteinUhlenbeckStrategy.cs`.
- **Live VM** extends `LiveSignalStrategyViewModelBase`; consumes `IMarketDataHub.Quotes(InstrumentId)`.
- **DI:** `services.AddOrnsteinUhlenbeckStrategy()` from `App.xaml.cs`; opened via `IStrategyFactory`.
