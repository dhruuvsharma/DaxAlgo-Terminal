# Backtesting

> Last updated: 2026-06-30

### In plain terms

**Backtesting means "replaying history to see how a strategy *would* have done."** You feed the engine
a recording of past market data, it plays it back tick by tick through a strategy, *pretends* to place
the strategy's orders into a simulated order book (charging realistic fees and slippage), and then
reports the result — the equity curve, the win rate, the worst drawdown, and a battery of performance
ratios. Nothing real is traded; it's a flight simulator for a strategy.

The terminal ships a tick-level backtest engine that runs the same strategies against historical parquet files. Strategies implement `IBacktestStrategy` and trade through an `IOrderRouter` — the same seam they would use live — so the engine measures simulated fills, P&L, equity curve, drawdown, and a broad performance suite.

For the strategy catalog and how to add new strategies, see [strategies.md](strategies.md). For the design rationale behind the engine, see [architecture.md](architecture.md).

## Surfaces

There are three ways to run a backtest:

- **Backtest Studio** (Tools → Backtest Studio) — the full graphical workbench: strategy picker, instrument + date range, run/cancel, a live equity curve, a trades grid, and the stats panel. Start here.
- **Quick backtest** — right-click any card in the strategy catalog → **Quick backtest (last 1 year)** for a one-click run on a default instrument, no setup.
- **CLI** (`daxalgo-backtest`) — headless and scriptable, for sweeps, walk-forward, and CI. Subcommands: `synth`, `run`, `sweep`, `walkforward`, `mc`, `tca`, `features`.
- **C++ Fast engine** (optional) — a "Use C++ Fast engine" checkbox when the C++ binary is built; only `meanReversion` is wired on the C++ side today. See [polyglot.md](polyglot.md).

## Isolated worker and packaged strategies

Supported Studio single runs execute in a one-shot `TradingTerminal.Backtest.Worker` process. The UI
writes a versioned request, receives bounded progress, and accepts results only after the worker publishes
a hashed terminal manifest. Cancellation, timeout, process failure, and malformed output terminate the
whole worker process tree rather than leaving heavy work inside the WPF shell.

Protocol v2 also accepts an immutable installed `.daxstrategy`. The client re-verifies the selected
installation under current trust policy and stages only its canonical manifest and headless engine
dependency closure. The worker validates those exact bytes again, creates the manifest-named
`IStrategyEngineFactory`, validates typed parameters against its schema, and runs the resulting
`IBacktestStrategy` through the same `TradingTerminal.Backtest.Engine` used by built-in strategies. No
second backtest implementation is required. See [strategy-bundles.md](strategy-bundles.md) for the trust,
store, and same-user containment boundaries.

Each request pins the deployed host `Backtest.Engine` assembly hash separately from the selected strategy
assembly hash. For installed bundles it also retains the host's closed trust evidence: either unsigned
local development, or a verified publisher key id plus the SHA-256 fingerprint of its trusted SPKI. The
worker reports both actual assembly hashes and the same trust evidence, and the client verifies them
against the request before accepting a successful result.

The worker's collectible load context and one-shot process tree provide cleanup and fault isolation, not
an OS sandbox: external strategy code still runs with the user's permissions. A verified signature proves
publisher endorsement of the signed content, while the archive hash records the exact store/client
selection; neither proves that code is safe. Marketplace strategies must remain trusted-publisher-only
until execution is constrained by a restricted token/AppContainer or VM, or by a separate constrained
strategy child with signal-only IPC. Result `StrategyAssemblyClosure` data describes the verified staged
engine closure, not proof that every listed assembly was loaded.

> 🖼️ **Screenshot:** `images/tool-backteststudio.png` — Backtest Studio with an equity curve and the stats panel populated.
> 🎬 **Video:** `images/video/backtest-studio.mp4` — a backtest run end-to-end.

## Generate a synthetic dataset and run

```powershell
dotnet build src\windows\Backtest\TradingTerminal.Backtest.Cli

$exe = "src\windows\Backtest\TradingTerminal.Backtest.Cli\bin\Debug\net9.0-windows\daxalgo-backtest.exe"

# Generate 10k ticks of a mean-reverting random walk with variable L1 sizes
& $exe synth --output bt-data.parquet --ticks 10000

# Run a strategy with maker/taker fees
& $exe run --strategy meanReversion --symbol TEST --data bt-data.parquet `
           --tick-size 0.01 --taker-fee 0.01 --maker-rebate 0.005

# Grid-sweep parameters in parallel
& $exe sweep --strategy meanReversion --symbol TEST --data bt-data.parquet `
             --lookback "50,100,200" --entry "0.05,0.10,0.20" --stop "0.20,0.40" `
             --output sweep.csv --parallel 8
```

`run` writes outputs to `./bt-results/` by default:

- `summary.json` — full stats block.
- `trades.csv` — every round-trip with entry/exit timestamps + prices + gross PnL.
- `equity.csv` — per-sample equity curve.
- `fills.csv` — per-fill record with simultaneous mid and liquidity flag (Maker/Taker). Input for TCA.

`sweep` writes a single CSV with 15 columns per parameter cell: Sharpe, Sortino, Calmar, Omega, max-drawdown, Ulcer index, max consecutive losses, win rate, profit factor, expectancy, fees, rebates, ending cash, etc.

## CLI subcommand reference

| Command | Purpose |
|---|---|
| `synth` | Generate a synthetic mean-reverting parquet tick file. |
| `run` | Single backtest. Emits `trades.csv` + `equity.csv` + `fills.csv` + `summary.json`. |
| `sweep` | Parameter-grid evaluation in parallel. Emits a CSV with one row per cell. |
| `walkforward` | Rolling train/test windows. Picks best param on train, evaluates OOS on test, emits per-window CSV. |
| `mc` | Bootstrap resample of a `trades.csv`. Reports distribution stats for Sharpe / MDD / final equity (Bailey-López-de-Prado style). |
| `tca` | Transaction-cost analysis from `fills.csv`. |
| `features` | Aggregate ticks → labelled feature CSV ready for any ML library (triple-barrier labelling). |

Run any command with no args to see its specific flags.

### Example end-to-end pipeline

```powershell
$exe = "src\windows\Backtest\TradingTerminal.Backtest.Cli\bin\Debug\net9.0-windows\daxalgo-backtest.exe"

# 1. Build a tape
& $exe synth --output ticks.parquet --ticks 100000

# 2. Pick the best params on a windowed walk-forward
& $exe walkforward --strategy meanReversion --symbol TEST --data ticks.parquet `
                   --windows 5 --train-fraction 0.7 --output wf.csv

# 3. Single run with the best config + maker rebate
& $exe run --strategy meanReversion --symbol TEST --data ticks.parquet `
           --maker-rebate 0.005 --taker-fee 0.01 --output .\final\

# 4. TCA on the run
& $exe tca --results .\final\ --output tca.json

# 5. Monte Carlo on the trade tape
& $exe mc --trades .\final\trades.csv --simulations 10000

# 6. Export labelled features for offline ML training
& $exe features --data ticks.parquet --output labelled.csv
```

## Fees, risk, and execution realism

### Fee models (`IFeeModel`)

Three concrete models ship in `Core/Trading/`:

- `ZeroFeeModel` (default) — no fees.
- `MakerTakerFeeModel` — per-unit rebates and fees. The simulated order book tags each fill as Maker (limit) or Taker (market/stop) via `OrderEvent.Liquidity`, so the right side of the schedule fires automatically.
- `BpsFeeModel` — flat basis points on notional.

CLI flags: `--taker-fee`, `--maker-rebate`, `--fee-bps`.

### Risk caps (`IRiskManager`)

`Core/Risk/RiskManager` enforces a per-symbol absolute-position cap and a per-UTC-day realised-loss cap. `BacktestOrderRouter` runs `Evaluate` before every submission; rejections surface as `OrderEvent` with `State=Rejected` on the strategy's existing event stream. Same accounting will be re-used by the live router when the OMS lands.

### Microstructure helpers (`Microstructure` in `Core/MarketData/`)

- **L1**: `Microprice`, `QueueImbalance`, `HalfSpread` (pure functions with `Tick` overloads).
- **L2** (consume `DepthSnapshot`): `CumulativeImbalance`, `WeightedMidPrice`, `SideDepth`, `EstimatedSlippage(side, qty, out fullyFilled)`, `LargestLevelGap`.

Plus `Indicators.{SimpleMovingAverage, RollingStdev, ExponentialMovingAverage, RelativeStrengthIndex, AverageTrueRange}` streaming primitives shared by the strategy library.

### L2 / depth-of-market

`Core/Domain/` has `DepthLevel(Price, Size)` and `DepthSnapshot(TimestampUtc, Bids, Asks)` records flowing through `IBrokerClient.SubscribeDepthAsync` and the repository (UI-marshalled). L2 depth is wired for **cTrader, Binance, Ironbeam, Upstox, the crypto venues (Coinbase / Bybit / Kraken / OKX), and the Simulated** backend; **IB (`reqMktDepth` not yet plumbed), NinjaTrader, Alpaca, and LSE** throw `NotSupportedException` on `SubscribeDepthAsync` and callers degrade to L1. The per-broker SQLite (`-l2.db`) and QuestDB store backends persist depth; see [market-data.md](market-data.md).

## Engine internals

| Component | Job |
|---|---|
| `BacktestSession` (Infrastructure) | Orchestrates the replay loop, advances `SimulatedClock`, evaluates `SimulatedOrderBook` against each tick, runs the optional `IRiskManager` before submission, and tracks P&L through `TradeLedger`. |
| `SimulatedOrderBook` (Infrastructure) | Holds resting orders; consults `L1FillModel` to decide which to fill on each tick. Tags fills Maker / Taker. |
| `L1FillModel` (Infrastructure) | Market orders cross the spread plus `slippageTicks × tickSize`; limit/stop fills when the relevant touch crosses the level. |
| `TradeLedger` (Infrastructure) | Tracks open positions, realised PnL, fees deducted per fill. |
| `ParquetTickReader` / `ParquetTickWriter` (Infrastructure) | Streaming, row-group buffered (default 50k rows). Columns: `TimestampMicros` (int64 µs), `Bid`, `Ask` (float64). |
| `StatisticsCalculator` (Infrastructure) | Sharpe/Sortino annualised from the median equity-sample gap. Plus Calmar (annualised CAGR / MDD), Omega (Σ gains / Σ losses), Ulcer index (RMS of percent drawdowns), recovery factor, downside deviation, max consecutive losses. |

## Recording live ticks for backtests

The Tools → Record live ticks window streams the active broker's live tick feed to a parquet file using the same `ParquetTickWriter` schema the engine reads. See [user-guide.md](user-guide.md#recording-live-ticks) for the click-through.

## Transaction-cost analysis (`tca`)

After a backtest, evaluate whether slippage is killing the strategy:

```powershell
$exe = "src\windows\Backtest\TradingTerminal.Backtest.Cli\bin\Debug\net9.0-windows\daxalgo-backtest.exe"
& $exe tca --results .\bt-results\ --output tca.json
```

Console output: TWAP mid, VWAP fill, implementation shortfall (signed; positive = cost vs TWAP benchmark), mean / VWAP-weighted slippage, slippage P50/P90/P99, maker/taker mix, and a per-UTC-hour breakdown of fills + mean slippage + maker fraction.
