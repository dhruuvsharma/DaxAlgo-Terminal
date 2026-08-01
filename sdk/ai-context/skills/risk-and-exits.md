---
id: risk-and-exits
name: Risk, position sizing and exits
triggers: stop, stop loss, take profit, target, trail, trailing, exit, risk, sizing, position size, drawdown, flatten, breakeven, r multiple, reward, time stop, max loss, kelly, atr
---

# Risk, sizing and exits

The engine simulates fills and reports signals; it does not manage risk for you. Exits are strategy code.

## Position state

Track your own signed position — the router does not hand it back to you synchronously.

```csharp
private long _position;   // signed units: -1 short, 0 flat, +1 long (in units of Quantity)
private int  _orderSeq;   // makes ClientOrderIds unique at identical timestamps

private string NextOrderId(IClock clock) => $"{StrategyId}-{clock.UtcNow:yyyyMMddHHmmssfff}-{_orderSeq++}";
```

Move straight to the target position: a reversal is **one** order for `2 * Quantity`, not a close and
then an open. Update `_position` when you send, and reconcile in `OnOrderEventAsync` if a fill differs.

## Exits, in order of how often they are got wrong

1. **Flatten in `OnEndAsync`.** A run that ends holding a position has not realised its P&L, and every
   statistic downstream is then a lie. This is not optional.
2. **Stops are prices, not distances.** Compute the stop level once at entry and store it. Recomputing it
   per tick from a moving reference silently turns a stop into a trailing stop.
3. **Trailing stops ratchet in one direction only.**
   `_stop = Math.Max(_stop, mid - k * atr)` for a long — never `Math.Min`, or it walks toward you.
4. **Volatility-scaled distances.** A stop in "3 ticks" is meaningless across instruments; express it in
   ATR, in stdev of returns, or in ticks *of that contract* read from the `Contract`.
5. **Time stops.** If the edge is a burst (order-flow ignition, a sweep), it decays: exit after N seconds
   of `clock.UtcNow` if the thesis has not paid. An HFT signal held for an hour is a different strategy.
6. **Microstructure exits.** Spread blowout (`spread > mean + 2*stdev`), delta flipping hard against the
   position, the book refilling on the side you were fading — these are exits, and they fire before a
   price stop does.

## Sizing

Keep it explicit and boring:

```csharp
public static StrategyParameterSchema Schema { get; } = new(
    StrategyParameter.Int("quantity", "Order size", 1, min: 1, max: 100),
    StrategyParameter.Number("stopAtr", "Stop (ATR multiples)", 2.0, min: 0.25, max: 10, step: 0.25),
    StrategyParameter.Int("maxPositions", "Max concurrent entries", 1, min: 1, max: 10));
```

Expose the risk knobs as parameters. That is what makes them sweepable in Backtest Studio instead of
being buried as magic numbers in the kernel.

## Guards that belong in every strategy

- Reject zero/negative or crossed quotes (`bid <= 0 || ask <= 0 || ask < bid`) before doing any maths.
- Do not enter without a warm-up (estimators are meaningless on their first samples).
- Do not stack entries unless the strategy is explicitly a scaling one — check `_position` first.
- One signal per condition transition, not one per tick while the condition remains true. Latch it.
