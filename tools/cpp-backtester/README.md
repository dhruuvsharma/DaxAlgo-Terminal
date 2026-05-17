# TickBacktester — High-Performance C++20 Tick-Level Backtesting Engine

A production-grade, event-driven backtesting engine for quantitative trading research.

## Features

- **Sub-microsecond event dispatch** via priority-queue-based event engine
- **Cache-friendly data structures** — `Tick` is exactly 64 bytes (one cache line)
- **Realistic execution simulation** — bid/ask spread, slippage, and latency models
- **Extensible strategy interface** — plug in any C++ strategy
- **Multiple data formats** — CSV and Apache Parquet
- **Rich performance metrics** — Sharpe, Sortino, Calmar, max drawdown, win rate
- **Multi-instrument support**

## Architecture

```
+--------------------------------------------------+
|                 BacktestRunner                   |
|    (orchestrates all components)                 |
+--------------------+-----------------------------+
                     |
        +------------v------------+
        |      EventEngine        |  Priority-queue event loop
        |  (TimestampedEvent PQ)  |  dispatches in timestamp order
        +--+----------+-----------+
           |          |
+----------v--+  +----v--------------------------------------+
| Exec        |  | StrategyAdapter                          |
| Simulator   |  |  +--------------+ +------------------+  |
| (fills)     |  |  |MarketDataBook| |     Strategy     |  |
+----------+--+  |  | (TickBuffers)| |   (user code)    |  |
           |     |  +--------------+ +------------------+  |
           |     +-----------------------------+------------+
           |                                   |
           |     +-----------------------------v------------+
           +---->|         OrderManager                     |
                 |  (orders, fills, cancellations)          |
                 +-----------------------------+------------+
                                               |
                 +-----------------------------v------------+
                 |           Portfolio                      |
                 |  (cash, positions, PnL tracking)         |
                 +------------------------------------------+
```

## Build

### Prerequisites

```bash
# Ubuntu/Debian
sudo apt install cmake ninja-build
sudo apt install libeigen3-dev libfmt-dev libspdlog-dev
sudo apt install libarrow-dev libparquet-dev

# macOS (Homebrew)
brew install cmake ninja eigen fmt spdlog apache-arrow

# Windows (vcpkg)
vcpkg install eigen3 fmt spdlog arrow[parquet]
```

### Compile

```bash
cd backtester
cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build --parallel
```

### Run

```bash
# Generate sample data and run backtest
./build/backtester

# Run with specific data file
./build/backtester my_ticks.csv

# Run performance benchmark
./build/benchmark

# Generate market data
./build/data_fetcher --source generate --symbol NIFTY --ticks 1000000 --out nifty.csv
./build/backtester nifty.csv
```

### JSON mode — embeddable polyglot bridge

`tick_backtester` is the same engine wrapped behind a JSON-in / JSON-out
interface, designed for embedding in a parent process (e.g. DaxAlgo Terminal's
C# WPF shell). It reads a single JSON object from stdin, runs the backtest,
and writes a single JSON object to stdout. All progress / info / error
output goes to stderr.

```bash
echo '{
  "strategy_id":            "meanReversion",
  "symbol":                 "TEST",
  "tick_data_parquet_path": "/tmp/ticks.parquet",
  "tick_size":              0.01,
  "contract_multiplier":    1.0,
  "starting_cash":          100000.0,
  "taker_fee_per_unit":     0.01,
  "params": { "lookback": 50, "entry_z": 2.0, "exit_z": 0.5, "trade_qty": 10 }
}' | ./build/tick_backtester
```

Returns:

```json
{
  "stats": { "total_return": 0.07, "sharpe": 1.83, "sortino": 2.41, "max_drawdown": 0.04,
             "trade_count": 142, "win_rate": 0.62, "profit_factor": 1.55, ... },
  "ending_cash":      107023.5,
  "total_fees":       28.42,
  "ticks_processed":  100000,
  "engine_milliseconds": 312.8,
  "equity_curve_parquet_path": null,
  "trades_parquet_path":       null
}
```

The parquet schema this mode reads is the one DaxAlgo Terminal's
`ParquetTickWriter` emits — columns `TimestampMicros` (int64 µs), `Bid`,
`Ask` (float64). The loader synthesises `last_price` from the mid when
those columns are absent. Strategy ids match the C# `BacktestStrategyOption.Id`
catalogue; today only `meanReversion` is wired on this side.

## Data Format

### CSV

```
timestamp,bid,ask,last,volume
1700000001000000000,19499.50,19500.50,19500.00,1523
1700000002000000000,19500.00,19501.00,19500.50,892
```

Timestamps are **nanoseconds since Unix epoch**.

### Parquet

Same columns, columnar format. Use `write_parquet()` to convert.

## Writing a Strategy

```cpp
#include "strategy/strategy.h"

class MyStrategy : public bt::Strategy {
public:
    MyStrategy() : Strategy("MyStrategy") {}

    void on_start(StrategyContext& ctx) override {
        // initialization
    }

    void on_tick(const bt::Tick& tick, StrategyContext& ctx) override {
        // your logic here
        if (should_buy(tick, ctx)) {
            buy_market(tick.instrument_id, 10.0, ctx);
        }
    }

    void on_order_fill(const bt::Fill& fill, StrategyContext& ctx) override {
        // react to fills
    }

private:
    bool should_buy(const bt::Tick& tick, StrategyContext& ctx) {
        auto* buf = ctx.market_data.buffer(tick.instrument_id);
        if (!buf || buf->size() < 20) return false;
        return tick.mid_price() < buf->sma(20);
    }
};
```

## Performance

Typical throughput on modern hardware (AMD Ryzen / Intel Core):

| Scenario | Throughput |
|---|---|
| Event publish + dispatch | ~8-12M ticks/sec |
| Full pipeline (data + OMS + exec) | ~3-6M ticks/sec |
| 1M ticks end-to-end | < 500ms |

Memory: 64 bytes per tick. 1M ticks = 64 MB.

## API Data Sources

| Market | Source | Notes |
|---|---|---|
| India (NIFTY, BANKNIFTY) | Upstox API | Free tier available |
| US Equities | Polygon.io, Alpha Vantage | Free tier |
| Crypto | Binance, Coinbase | Public REST API |

See `app/data_fetcher.cpp` for integration points.
