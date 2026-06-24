# Order Book

`TradingTerminal.OrderBook` — **Charts → Order book…**

> Live L2 depth ladder for one instrument: asks stacked above the spread, bids below, with
> per-level size bars. Renders every `DepthSnapshot` the hub publishes — no polling, no
> aggregation, the broker's book as-is.

## Data requirements

| L1 | Bars | Depth | Trade tape |
|:--:|:--:|:--:|:--:|
| — | — | ✅ | — |

Depth-capable brokers: **IB** (market depth), **cTrader**, **Binance**, **Ironbeam**,
**Simulated**. Alpaca, NinjaTrader and London Strategic Edge do not serve L2 — the window stays
empty with the reason in the status line.

## Settings

| Setting | Values | What it does |
|---|---|---|
| Instrument | searchable picker | subscribes the depth stream via `IMarketDataIngest` |

That's the only input — the ladder shows whatever levels the broker sends (typically 5–20 per
side).

## Colors & layout

| Color | Element |
|---|---|
| `#26A69A` teal text | bid prices/sizes |
| `#EF5350` red text | ask prices/sizes |
| `#26A69A` @ 20% bar | bid size bar |
| `#EF5350` @ 20% bar | ask size bar |

Size bars are normalized to the **largest level on either side** (so bid/ask wall sizes compare
visually), and each row also shows the **cumulative size** from the top of book outward. Asks are
displayed highest-price-first so the best ask sits just above the spread; bids best-first below —
the spread sits visually between the two stacks.

## Read-outs

| Value | Definition |
|---|---|
| Best bid / Best ask | top of book from the latest snapshot |
| Spread | best ask − best bid |
| Mid | (best bid + best ask) / 2 |
| Bid levels / Ask levels | level count per side in the snapshot |
| Last update | snapshot timestamp (UTC) |

## Code map

| What | Where |
|---|---|
| VM (depth stream → ladder rows) | `OrderBookViewModel.cs` |
| Ladder UI | `OrderBookWindow.xaml` |
| Depth seam | `IBrokerClient.SubscribeDepthAsync` → ingest → `IMarketDataHub.Depth(InstrumentId)` |
