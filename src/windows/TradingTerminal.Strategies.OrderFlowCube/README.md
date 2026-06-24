# Order Flow Cube

`TradingTerminal.Strategies.OrderFlowCube` — live signal window for strategy id **`orderflow.cube`**.

> Phase-space view of order flow: CVD imbalance (trend window) vs aggressor ratio (recent window) with size-ratio markers. Detects institutional accumulation/distribution regimes from the trade tape. **Data/signals only — does not place orders.**

First strategy in the regime-cube series (see `ideas.md`) and the canonical Helix Toolkit 3D-scatter template.

## Data requirements

| L1 | Bars | Depth | Trade tape |
|:--:|:--:|:--:|:--:|
| ✅ | ✅ | — | ✅ |

**Requires the trade tape** (`OnTradeAsync`). Trade-tape is opt-in per broker — wired on **IB / Binance / Ironbeam** (+ crypto venues, Simulated); NT/cTrader/Alpaca/LSE throw `NotSupportedException`, so the strategy fails loudly there.

## How it works

Consumes the trade tape and maintains three orthogonal-ish axes:

- **CVD imbalance** ∈ [-1, +1] — signed-flow / total-flow over the rolling window. Positive ⇒ buyers in control.
- **Aggressor ratio** ∈ [0, 1] — buy-aggressor volume / total. 0.5 = balanced.
- **Size ratio** — window mean trade size / baseline mean size. > 1 ⇒ recent trades are larger than the long baseline ("institutional-sized" prints).

The cube has 8 octants; v1 trades the two clearest:

- **Institutional accumulation (long)** — positive CVD ∧ buy-dominant aggressor ∧ larger-than-baseline size.
- **Institutional distribution (short)** — mirror image.

Exit when CVD reverses across the threshold against the position, or after `HoldTrades` trades (time stop). Trades flagged `AggressorSide.Unknown` feed the size baseline only.

> Note: CVD and aggressor ratio over the same window are mathematically linked (`cvd ≈ 2·aggressor − 1`). For truly orthogonal axes, run the three over different windows — left as a tunable.

## Parameters (engine defaults)

| Param | Default | Meaning |
|---|--:|---|
| `windowTrades` | 200 | rolling signal window |
| `baselineTrades` | 2000 | long size baseline |
| `cvdImbalanceThreshold` | 0.40 | CVD entry floor |
| `aggressorBuyThreshold` | 0.60 | aggressor-ratio entry floor |
| `sizeRatioThreshold` | 1.20 | size-ratio entry floor |
| `holdTrades` | 200 | time-stop in trades |
| `quantity` | 1 | order size |

## Project layout

| File | Role |
|---|---|
| `OrderFlowCubeStrategy.cs` | `ITradingStrategy` metadata + data-requirement tags |
| `OrderFlowCubeCalculator.cs` | rolling CVD / aggressor / size-ratio math |
| `QuoteDerivedTradeSynthesizer.cs` | synthesizes trades from quotes where no tape is available |
| `OrderFlowCubeViewModel.cs` | live VM + Helix 3D scatter state |
| `OrderFlowCubeWindow.xaml(.cs)` | dashboard view |
| `DependencyInjection.cs` | `AddOrderFlowCubeStrategy()` |

## Wiring

- **Engine impl:** `src/TradingTerminal.Infrastructure/Backtest/Strategies/OrderFlowCubeStrategy.cs`.
- **Live VM** extends `LiveSignalStrategyViewModelBase`; must check trade-tape capability before subscribing.
- **DI:** `services.AddOrderFlowCubeStrategy()` from `App.xaml.cs`; opened via `IStrategyFactory`.
