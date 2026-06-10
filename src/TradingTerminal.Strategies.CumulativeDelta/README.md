# Cumulative Delta Scalper

`TradingTerminal.Strategies.CumulativeDelta` — live signal window for strategy id **`cumulative.delta.scalper`**.

> Sniper-mode tick delta scalper. Bid-tick uptick/downtick → bar deltas → window crossover of ±threshold, gated by 5 confirmations (momentum, HTF EMA, EMA slope, ADX, dynamic spread). Multi-session GMT (Asia/London/NY/Overlap), per-session and daily caps, inter-signal cooldown. **Display only — does not place orders.**

## Data requirements

| L1 | Bars | Depth | Trade tape |
|:--:|:--:|:--:|:--:|
| ✅ | ✅ | — | — |

L1 quotes drive the bid-tick uptick/downtick rule; OHLCV bars drive the chart-TF delta window, ATR, and HTF EMA/ADX. No depth or trade-tape feed is consumed — runs on every broker.

## How it works

Each L1 tick is classified up/down (bid-tick rule) and accumulated into a per-bar **delta**. A rolling window of bar deltas is watched for a **crossover** of ±threshold. A candidate signal must clear **5 confirmation gates**:

1. **Momentum** — short-term price thrust agrees with the delta side.
2. **HTF EMA** — higher-timeframe trend filter.
3. **EMA slope** — trend is actually moving, not flat.
4. **ADX** — trend strength floor.
5. **Dynamic spread** — entry blocked when the spread is too wide.

Session logic is GMT-bucketed (Asia / London / NY / Overlap) with per-session and daily signal caps plus an inter-signal cooldown.

## Project layout

| File | Role |
|---|---|
| `CumulativeDeltaStrategy.cs` | `ITradingStrategy` metadata (id, display name, data-requirement tags) |
| `Indicators.cs` | EMA / ADX / ATR / delta helpers |
| `CumulativeDeltaViewModel.cs` | Live VM — delta accumulation, confirmations, session state |
| `CumulativeDeltaWindow.xaml(.cs)` | Dashboard view |
| `DependencyInjection.cs` | `AddCumulativeDeltaStrategy()` |

## Wiring

- **Engine impl: none.** This is a **live-only** strategy — there is no `IBacktestStrategy` under `Infrastructure/Backtest/Strategies/`, so it does not appear in the Backtest tab or CLI. All logic lives in the VM + `Indicators.cs`.
- **Live VM** extends `LiveSignalStrategyViewModelBase` and consumes `IMarketDataHub.Quotes/Bars(InstrumentId)`.
- **DI:** `services.AddCumulativeDeltaStrategy()` from `App.xaml.cs`; opened via `IStrategyFactory`.
