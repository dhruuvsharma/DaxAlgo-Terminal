# SigmaIcFlow backtest harness

A small Python harness that backtests the **Σ⁻¹·IC Order-Flow Optimizer** (`sigmaIcFlow`) strategy on
crypto and CME index futures. It fetches data, writes it as CSV in the schema the C# backtest engine
reads, and drives the **real engine** through `daxalgo-backtest.exe` — it does *not* reimplement the
strategy.

```
fetch data ──► quotes.csv (+ trades.csv) ──► daxalgo-backtest run --strategy sigmaIcFlow ──► summary.json ──► table
```

## Data paths (and their honesty)

| Instruments | Source | Tape | Quality |
|---|---|---|---|
| BTC, ETH | Binance public REST `aggTrades` | **real trade tape** + synthesised L1 | `q = 1.0` — the strategy's flow signals get genuine aggressor flow |
| MES, MNQ, ES, NQ | Yahoo Finance 1-minute bars | synthetic quote path (O→H→L→C), **no tape** | `q ≈ 0.4` — engine's honest synthetic-L1 fallback; **indicative only** |

There is no free real tick tape for index futures, so those numbers are a sanity check, not a true
microstructure backtest. Yahoo only serves ~7 days of 1-minute history.

## Setup

```bash
# 1. Build the C# CLI once (the harness auto-detects the exe under bin/):
dotnet build src/TradingTerminal.Backtest.Cli

# 2. Python deps — crypto needs NONE (stdlib only); futures need yfinance:
pip install -r tools/backtest/requirements.txt
```

## Usage

```bash
# Crypto (real Binance tape) + futures (synthetic), last 24h crypto / 5d futures:
python tools/backtest/backtest_sigmaicflow.py --instruments BTC,ETH,MES,MNQ

# Just BTC, a quick 6-hour window:
python tools/backtest/backtest_sigmaicflow.py --instruments BTC --hours 6 --out ./bt

# List known instruments:
python tools/backtest/backtest_sigmaicflow.py --list
```

Key flags: `--hours` (crypto window), `--days` (futures, ≤7), `--max-trades` (crypto fetch cap),
`--starting-cash`, `--cli <path>`, `--binance-base` (use `https://data-api.binance.vision` if the
main endpoint is geo-blocked). Run with `-h` for the full list.

Output: a comparison table on stdout, per-instrument `summary.json` / `trades.csv` / `equity.csv`
under `--out/<symbol>/`, and an aggregated `--out/summary.csv`.

## Expect a slow warm-up

SigmaIcFlow is deliberately conservative: it stays in **bootstrap mode** until its isotonic
calibration has ~500 observations (one per 1-minute bar), and every entry must clear a full
round-trip cost + a first-passage EV check. So:

- Use a long enough window — **a day or more of crypto tape** to get past warm-up and see real trades.
  A 3–6 hour window mostly stays in bootstrap and may show few/zero trades. That's by design.
- Fetching the real Binance tape is the slow part (a day of BTC is millions of trades). Start small
  (`--hours 6`) to smoke-test the plumbing, then widen.
- Zero trades on a short or low-volatility window is normal, not a bug — the strategy refuses setups
  that don't clear costs.

## How it plugs into the engine

The C# side gained an optional **trade tape** for parquet/CSV-source backtests: `run --data
<quotes> --trades <tape>` merges quotes and trades by event time (mirroring the local-store path),
so trade-tape-primary strategies replay genuine prints instead of synthetic L1. The interop format is
CSV (`CsvTickReader` / `CsvTradeReader`) — chosen over parquet because parquet footer schemas don't
round-trip cleanly between pyarrow and the engine's Parquet.Net version. The engine's native parquet
readers/writers are unchanged.
```
quotes.csv : timestamp_micros,bid,ask,bid_size,ask_size      (epoch µs UTC)
trades.csv : timestamp_micros,price,size,aggressor            (aggressor 1=Buy 2=Sell 0=Unknown)
```
