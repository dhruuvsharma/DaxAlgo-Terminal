---
id: order-flow
name: Order flow, footprint and book microstructure
triggers: order flow, orderflow, imbalance, footprint, vpoc, poc, volume profile, delta, cvd, cumulative delta, depth, order book, book, dom, liquidity, sweep, iceberg, absorption, tape, aggressive, passive, bid ask, spoof, vacuum, stacked, ladder, microstructure, hft, scalp
---

# Order flow, footprint and book microstructure

What the host actually gives you, and how to compute the usual constructs correctly on top of it.

## The raw events

| Event | When | Fields you get |
|---|---|---|
| `Tick` (L1) | best bid/ask changed | `TimestampUtc, Bid, Ask, BidSize, AskSize` |
| `TradePrint` (tape) | an aggressive fill printed | price, size, timestamp, and an **aggressor side if the broker supplies one** |
| `DepthSnapshot` (L2) | book changed | the top N levels per side (price + size), newest snapshot wins |

`OnTradeAsync` and `OnDepthAsync` only fire if you declare `TradeTape` / `Depth` in `DataRequirement`.
Declaring them costs nothing when the broker supplies them, and the host will not offer a broker that
can't.

## Signing trades (the single most common bug)

Not every feed labels the aggressor. When it doesn't, sign with the **tick rule against the prevailing
quote**, and keep the last quote yourself:

```csharp
// _bid/_ask updated in OnTickAsync BEFORE the trade is signed.
var mid = (_bid + _ask) / 2.0;
var side = t.Price >= _ask ? +1        // lifted the offer  -> buy-side aggression
         : t.Price <= _bid ? -1        // hit the bid       -> sell-side aggression
         : t.Price > mid  ? +1 : -1;   // inside the spread -> lean on the mid
```

Do NOT sign by comparing to the previous *trade* price: that is the classic tick test and it misclassifies
badly in fast markets. If the feed does give you an aggressor flag, use it and skip all of this.

## Footprint constructs

- **Volume delta at a price** `delta_p = buyVolume_p - sellVolume_p`, accumulated per price level in the
  current bar/bucket. Bucket by `price / tickSize` rounded — never by raw double equality.
- **Imbalance ratio** `IR_p = buy_p / max(sell_p, 1)` (and its mirror). Guard the denominator; a zero on
  one side is not an infinite imbalance, it is a thin level. Require a **minimum volume** on the level
  before the ratio means anything (e.g. `buy_p + sell_p >= minLevelVolume`), or noise at the extremes will
  fire constantly.
- **Stacked imbalance** = N consecutive price levels all imbalanced the same way. Walk levels in price
  order; reset the run counter on any level that fails the ratio OR the minimum-volume floor.
- **VPOC** = the price with the most volume in the session/window: `argmax_p volume_p`. Recompute
  incrementally (keep a running max) — never re-scan the whole profile per tick.
- **CVD** = running sum of signed trade volume. It is a *level*, not a rate; compare it to its own recent
  history (a z-score or a slope), not to an absolute threshold.

## Book constructs

- **Depth at N levels** = sum of sizes over the top N. Snapshots arrive whole, so recompute per snapshot;
  don't try to diff them.
- **Liquidity vacuum / stacking**: a *relative* change over a short window —
  `(depth_now - depth_then) / max(depth_then, epsilon)`. Keep a small ring buffer of
  `(timestamp, depth)` and find the entry nearest `now - window`. Guard `depth_then == 0`.
- **Queue imbalance** `(bidDepth - askDepth) / (bidDepth + askDepth)` — bounded in [-1, 1], which is why
  it is the one book feature that behaves like a signal out of the box.

## Pitfalls that will silently ruin a strategy

- **Depth snapshots are not order lifecycles.** You cannot see individual orders being added or pulled,
  so you cannot detect spoofing or true icebergs directly — only the aggregate size change. Say so in the
  strategy's description rather than pretending otherwise.
- **A 100 ms window is not 100 ms of ticks.** Drive every time window off `clock.UtcNow` (never
  `DateTime.UtcNow`, never a tick count), because a backtest replays historical time.
- **Sub-second windows need bounded buffers.** A ring buffer sized to the window, not a `List` you append
  to forever — `OnTickAsync` and `OnTradeAsync` run per event.
- **Not every broker signs the tape.** Check the strategy's `DataRequirement` and fail loudly at setup if
  the feed can't supply what the math needs.
