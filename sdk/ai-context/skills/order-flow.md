---
id: order-flow
name: Order flow, footprint and book microstructure
triggers: order flow, orderflow, imbalance, footprint, vpoc, poc, volume profile, delta, cvd, cumulative delta, depth, order book, book, dom, liquidity, sweep, iceberg, absorption, tape, aggressive, passive, bid ask, spoof, vacuum, stacked, ladder, microstructure, hft, scalp, microprice, vpin, toxicity, kyle, lambda, queue
---

# Order flow, footprint and book microstructure

## The events

| Callback | You receive |
|---|---|
| `OnQuoteAsync` | `Quote` — `Bid`, `Ask`, `BidSize`, `AskSize`, `EventTimeUtc`, computed `Mid` and `Spread` |
| `OnTradeAsync` | `TradePrint` — `Price`, `Size`, `Aggressor`, `EventTimeUtc` |
| `OnDepthAsync` | `DepthSnapshot` — `Bids`/`Asks` best-first, plus `BestBid`/`BestAsk`/`BestBidSize`/`BestAskSize` |

**It is `Quote`, not `Tick`.** `Tick` is the retired broker-facing record and still exists, so writing
it compiles and binds nothing. Tape and depth fire only if `DataRequirement` declares them.

## Do not hand-roll these

All in `DaxAlgo.Sdk.Quant`: ambient, streaming, warm-up gated, tested.

| Construct | Use |
|---|---|
| Signing a trade | `TradeClassifier.Classify(trade, quote)` |
| Signed volume / CVD | `OrderFlowImbalance` — `Value` normalised, `Cumulative` for the line |
| Flow toxicity | `Vpin` |
| Price impact | `KyleLambda` |
| Fair value in a book | `Book.Microprice` |
| Queue imbalance | `Book.Imbalance(quote)` / `Book.Imbalance(depth, levels)` |
| Depth over N levels | `Book.DepthTotal(side, levels)` |
| Cost of taking size | `Book.SweepPrice` / `Book.SweepSlippage` |
| Is the spread unusual | `SpreadStats.IsWide()` |

```csharp
var side = TradeClassifier.Classify(trade, _lastQuote);  // venue's flag first, quote rule after
_flow.Update(trade.Size, side);                          // OrderFlowImbalance
var micro = Book.Microprice(depth);
var queue = Book.Imbalance(depth, levels: 5);
var cost  = Book.SweepPrice(depth.Asks, 50d);            // 0 = CANNOT fill, not "cheap"
```

Keep the last `Quote` in a field: classification needs the book as it stood when the print landed.

**Quote rule ≠ tick rule.** The quote rule compares the print to the prevailing bid and ask; the tick
rule compares it to the previous *trade* and misclassifies badly in fast markets.
`TradeClassifier.TickRule` is for feeds with no quote at all, and nothing else.

**Measure edge from the microprice, not the mid.** The mid reads the same whether ten lots are bid
against a thousand offered or the reverse — exactly when the next print is predictable. And read
imbalance deeper than the touch: one surviving five levels is closer to intent than one a single order
creates and cancels.

## What you still build

The footprint is per-price-level bookkeeping — a data structure, not an estimator.

- **Delta at a price** — `buy_p − sell_p` per level in the bar. Bucket by `Num.RoundToTick`, never by
  raw `double` equality.
- **Imbalance ratio** — `buy_p / max(sell_p, 1)` and its mirror, gated on a **minimum volume for the
  level**, or the thin extremes fire constantly. A zero on one side is a thin level, not infinity.
- **Stacked imbalance** — N consecutive levels imbalanced the same way; reset the run on any level
  failing the ratio or the volume floor.
- **VPOC** — `argmax_p volume_p`, kept as a running max, never re-scanned per tick.
- **Liquidity vacuum** — `(depth_now − depth_then) / max(depth_then, ε)` over a short window; a small
  ring of `(timestamp, depth)` and `Num.SafeDiv`.

`Footprint`, `VolumeProfile`, `Ladder`, `DepthCurve` and `Tape` draw all of it.

## Pitfalls

- **Depth snapshots are not order lifecycles.** Aggregate size per level, not orders added or pulled —
  so spoofing and true icebergs are not detectable, only that size changed. Say so in the description.
- **A 100 ms window is not 100 ms of ticks.** Drive windows off `context.Clock.UtcNow`, never
  `DateTime.UtcNow` and never an event count; a replay moves time differently.
- **Bound every sub-second buffer.** `RollingWindow`, or a ring sized to the window — these callbacks
  run hundreds of times a second for as long as the window is open.
- **CVD is a level, not a rate.** Compare it to its own history (`ZScore` over `Cumulative`), never to
  an absolute threshold, which differs on every instrument.
- **Not every broker signs the tape.** `Classify` copes, but if the edge depends on accurate signing,
  say so: an unsigned feed degrades the strategy rather than breaking it, and the user should know.
