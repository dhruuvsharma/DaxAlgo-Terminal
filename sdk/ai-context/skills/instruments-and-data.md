---
id: instruments-and-data
name: Instruments, brokers and what data you can actually get
triggers: instrument, contract, tick size, ticksize, symbol, futures, equities, crypto, forex, broker, feed, data requirement, l1, l2, bars, timeframe, session, exchange, binance, interactive brokers, ib, market hours
---

# Instruments, brokers, and what data you can actually get

## The contract

`Contract` names the instrument the strategy is running on; it is handed to the kernel's constructor and
to `BuildStrategy`. **Never hard-code a symbol, a tick size, or a multiplier.** A strategy that assumes
0.25-tick ES will do something quietly wrong on BTC. Derive levels from the contract and from the data:
bucket prices by the instrument's tick size, express distances in ATR or in stdev of returns.

## Declaring what you need

`StrategyDataRequirement` is a `[Flags]` enum — declare exactly what you consume and nothing more:

| Flag | Gives you | Cost of asking for it |
|---|---|---|
| `L1` | `OnTickAsync` (best bid/ask + sizes) | none; every broker has it |
| `Bars` | warm-up history + the bar series | none |
| `Depth` | `OnDepthAsync` (top-N book) | narrows you to brokers that stream L2 |
| `TradeTape` | `OnTradeAsync` (prints) | narrows you to brokers that stream the tape |

The host starts exactly the pumps you declare, and only offers brokers that can supply them. Asking for
`Depth | TradeTape` on a broker that has neither is not a runtime error you can recover from — it is a
strategy that will never be offered that broker.

## What the feeds actually are

- **Tape and depth are opt-in per broker.** Interactive Brokers, Binance and Ironbeam stream trades;
  several backends do not stream them at all. Depth is likewise partial.
- **Crypto (Binance et al.) is the easiest full-fidelity feed** — bars, L1, L2 and tape, keyless — which is
  why it is the default for testing an order-flow strategy.
- **The Simulated broker** always exists (synthetic random walk, or replay of the local store), so a
  strategy can be exercised offline. It is behind the amber SIMULATED DATA banner; do not treat its
  numbers as real.
- **A backtest replays the local store.** If the store has no depth for that instrument, a depth strategy
  backtests on nothing — the data has to have been recorded.

## Time

`IClock` is the only clock. In a backtest it is historical time moving at replay speed; `DateTime.UtcNow`
is wall-clock and is simply wrong there. Every window, timeout, cooldown and time-stop reads `clock.UtcNow`.

Bar boundaries, session opens and holidays are not handed to you — if a strategy is session-sensitive
(an opening-range break, say), it must derive the session from the timestamps it sees, and you should say
so in the description rather than silently assuming a 09:30 US open.
