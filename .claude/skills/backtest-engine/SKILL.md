---
name: backtest-engine
description: Internals of DaxAlgo Terminal's tick-level backtest engine — IBacktestStrategy seam, IOrderRouter, SimulatedOrderBook, L1FillModel, IFeeModel (Zero/MakerTaker/Bps), IRiskManager, ParquetTickReader/Writer, StatisticsCalculator, and the live/backtest router split. Use when touching fee models, risk caps, fill simulation, OMS stubs, the Backtest CLI (daxalgo-backtest.exe), or the Tools → Backtest tab. Skip for pure UI work or for adding strategies (use add-strategy instead).
---

# Backtest Engine

## Layout

- `Core/Backtest/` — `IBacktestStrategy` (engine seam), `BacktestConfig`, `BacktestResult`.
- `Core/Trading/` — `IOrderRouter`, `IFeeModel`, `IRiskManager`, `OrderEvent`, `Liquidity` enum.
- `Infrastructure/Backtest/` — `BacktestSession`, `SimulatedOrderBook`, `L1FillModel`, `TradeLedger`, `StatisticsCalculator`.
- `Infrastructure/Backtest/Persistence/` — `ParquetTickReader` / `ParquetTickWriter` (row-group buffered; epoch-microsecond timestamps).
- `Infrastructure/Backtest/Strategies/` — engine-side strategy impls.
- `App/Backtest/` — Tools → Backtest tab.
- `src/TradingTerminal.Backtest.Cli/` — headless `daxalgo-backtest.exe` (`run` / `synth` / `sweep` subcommands).

## Order routing

- **Live**: `LiveOrderRouter` delegates to the active broker via `IBrokerClient`. **NOTE**: today all three real clients throw `NotSupportedException` for orders. Don't wire a `LiveOrderRouter → broker.PlaceOrderAsync` path until OMS lands.
- **Backtest**: `BacktestOrderRouter` runs each submission through the optional `IRiskManager`, then pushes accepted orders into `SimulatedOrderBook`, evaluated by `L1FillModel` on every tick.
- When OMS lands, wire the **same `IRiskManager`** into `LiveOrderRouter` so live and backtest share accounting.

## Fees (`IFeeModel`)

- `Fee(side, qty, price, liquidity)` is the signature. Liquidity is `Maker | Taker`.
- Built-ins: `ZeroFeeModel`, `MakerTakerFeeModel`, `BpsFeeModel`.
- `SimulatedOrderBook` tags each fill: Limit ⇒ Maker, Market/Stop ⇒ Taker — set on `OrderEvent.Liquidity`.
- `TradeLedger` charges fees per fill, surfaces total on `BacktestResult.TotalFees`.
- CLI flags: `--taker-fee`, `--maker-rebate`, `--fee-bps`.
- Pass via `BacktestConfig.FeeModel`.

## Risk (`IRiskManager`)

- Per-symbol abs-position cap + per-UTC-day realised-loss cap.
- Wired via `BacktestSession.RunAsync(config, strategy, risk, ct)`.
- **Default**: zero caps ⇒ accept everything (matches legacy backtests). Don't silently change this.
- Rejections appear on the strategy's `OnOrderEventAsync` stream with `State=Rejected`. The strategy decides whether to retry / size down / give up.

## Tick data

- Parquet, epoch-microsecond timestamps, row-group buffered.
- Synth subcommand generates a mean-reverting random walk with variable L1 sizes and occasional spread-bursts — so microstructure / market-maker strategies actually exercise their logic.

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

## Quick commands

```powershell
# Generate synthetic ticks
src\TradingTerminal.Backtest.Cli\bin\Debug\net9.0-windows\daxalgo-backtest.exe synth --output bt-data.parquet --ticks 10000

# Run a strategy
src\TradingTerminal.Backtest.Cli\bin\Debug\net9.0-windows\daxalgo-backtest.exe run --strategy meanReversion --symbol TEST --data bt-data.parquet

# Sweep parameters in parallel
src\TradingTerminal.Backtest.Cli\bin\Debug\net9.0-windows\daxalgo-backtest.exe sweep --strategy bollinger --data bt-data.parquet
```

See also: [add-strategy](../add-strategy/SKILL.md) for the strategy authoring recipe.
