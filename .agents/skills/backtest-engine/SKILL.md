---
name: backtest-engine
description: Internals of DaxAlgo Terminal's tick/event backtest stack — Core contracts, TradingTerminal.Backtest.Engine replay and optimization, Infrastructure persistence adapters, and the Backtest/BacktestStudio tools. Use for fee/risk/fill simulation, event ordering, reports, optimization, or backtest surfaces. Skip strategy authoring; use add-strategy for external plugins.
---

# Backtest Engine

## Layout

- `src/windows/Core/TradingTerminal.Core/Backtest/` and `Trading/` — public contracts, configuration, results, fees and order routing seams.
- `src/windows/Backtest/TradingTerminal.Backtest.Engine/` — event replay, simulation clock/context, kernels, reports, optimization and polyglot adapters.
- `src/windows/Pipeline/TradingTerminal.Infrastructure/Backtest/` — persistence/feed adapters and remaining infrastructure integration.
- `src/windows/Tools/TradingTerminal.Backtest/` — quick backtest surface.
- `src/windows/Tools/TradingTerminal.BacktestStudio/` — catalog, data-source, optimization and walk-forward UI.

## Order routing

- **Live**: `LiveOrderRouter` delegates to the active broker via `IBrokerClient`. **NOTE**: today all three real clients throw `NotSupportedException` for orders. Don't wire a `LiveOrderRouter → broker.PlaceOrderAsync` path until OMS lands.
- **Backtest**: `BacktestOrderRouter` runs each submission through the optional `IRiskManager`, then pushes accepted orders into `SimulatedOrderBook`, evaluated by `L1FillModel` on every tick.
- When OMS lands, wire the **same `IRiskManager`** into `LiveOrderRouter` so live and backtest share accounting.

## Fees (`IFeeModel`)

- `Fee(side, qty, price, liquidity)` is the signature. Liquidity is `Maker | Taker`.
- Built-ins: `ZeroFeeModel`, `MakerTakerFeeModel`, `BpsFeeModel`.
- `SimulatedOrderBook` tags each fill: Limit ⇒ Maker, Market/Stop ⇒ Taker — set on `OrderEvent.Liquidity`.
- `TradeLedger` charges fees per fill, surfaces total on `BacktestResult.TotalFees`.
- Pass via `BacktestConfig.FeeModel`.

## Risk (`IRiskManager`)

- Per-symbol abs-position cap + per-UTC-day realised-loss cap.
- Wired via `BacktestSession.RunAsync(config, strategy, risk, ct)`.
- **Default**: zero caps ⇒ accept everything (matches legacy backtests). Don't silently change this.
- Rejections appear on the strategy's `OnOrderEventAsync` stream with `State=Rejected`. The strategy decides whether to retry / size down / give up.

## Tick data

- Parquet, epoch-microsecond timestamps, row-group buffered. **New code reads through `IMarketDataStore`** ([market-data-pipeline](../market-data-pipeline/SKILL.md)); parquet stays only for the recorder + a few AI/ML/Research tabs that haven't been migrated.
- `BacktestTickSource` does a k-way merge of quote and trade streams via `BacktestEvent` so `OnTickAsync` and `OnTradeAsync` fire in event-time order.
- Use the Studio or plugin harness synthetic feeds to exercise quotes, trades and depth deterministically.

## Stats

`StatisticsCalculator` (annualised from the median equity-sample gap):

- Sharpe, Sortino
- Calmar (annualised CAGR / MDD)
- Omega (Σ gains / Σ losses)
- Ulcer index (RMS of pct drawdowns)
- Recovery factor
- Downside deviation
- Max consecutive losses
- Max drawdown as fraction of peak
- Per-trade: win-rate, profit-factor, expectancy

Equity is sampled at most once per minute of simulated time.

## Hard rules

- **Strategies never call `IBrokerClient` directly** — only `IOrderRouter`.
- **Reuse `Indicators` and `Microstructure`** from `Core/MarketData/` — don't reimplement.
- **L2 strategies compute on L1 sizes today.** Swap to `DepthSnapshot` via the `Microstructure` multi-level helpers when L2 ticks land in the backtest engine. (Engine currently L1-only.)
- **OMS seam**: `IBrokerClient.PlaceOrderAsync` / `CancelOrderAsync` / `OrderEvents` exist but throw. Don't pretend they work yet.

## Verification

Build the affected Windows edition filter for tool-only work; build `TradingTerminal.Windows.slnx` for
Core/engine signatures. Run focused engine/tool tests and the external plugin harness when strategy
compatibility changes.

See also: [add-strategy](../add-strategy/SKILL.md) for the strategy authoring recipe.
