# Ten strategy briefs

One per category, chosen so that between them they exercise every data requirement the sandbox
supports and most of the drawing library. Each names a strategy people actually trade and a public
description of it, so the target is checkable rather than a matter of taste.

**The briefs name no type, no callback and no widget.** A brief that names the SDK tests whether a
model can transcribe. These test whether it can build a window from a description of the window.

Read [README.md](README.md) first for the two tiers, the rubric and the run protocol.

---

## S1 · Indicator confluence

**Category** technicals · **Data** bars · **Test on** SPY or ES, 1-minute
**Reference** [Day trading strategies survey](https://www.quantifiedstrategies.com/day-trading-strategies/) ·
the layout is the default TradingView chart with two indicators and one oscillator pane.

**Visual target.** Candles filling most of the height with two moving averages drawn over them in
different colours; a separate pane underneath on its own scale carrying the oscillator with its
overbought and oversold lines shaded; entry and exit marks on the candles at the bars where the
rules fired; a header line naming the instrument and the interval.

> **ASK** — `a strategy that trades EMA crossovers with an RSI filter`

> **SPEC** — Trade a fast and a slow moving-average crossover, but only take the signal when a
> momentum oscillator agrees: long when the fast crosses above the slow and the oscillator is above
> its midline and not yet overbought, short on the mirror. Show me the candles with both averages
> drawn on them, an oscillator pane below with its 30 and 70 lines shaded, and a marker on the bar
> where each entry and exit happened. Put the current values of both averages and the oscillator in
> a readout, and exit on the opposite cross.

**Accept when** the oscillator is on its **own** scale in its own pane and not flattened onto the
price axis · both averages are visibly different periods · the markers sit on the crossover bars and
not one bar late · the shaded zones are at the oscillator's thresholds.

**Watch for** an oscillator drawn on the price scale (a flat line near the price), and averages
computed on every tick rather than per bar.

---

## S2 · Candlestick reversal

**Category** candles / price action · **Data** bars · **Test on** ES or BTCUSDT, 5-minute
**Reference** [TradingView chart types](https://www.tradingview.com/support/solutions/43000703407-chart-types-available-on-tradingview/)

**Visual target.** A clean candle chart where the pattern bars are outlined or tinted so they stand
out from their neighbours, with a small label on each naming the pattern, and the level the trade
triggers at drawn across as a line until it is hit.

> **ASK** — `a candlestick pattern strategy`

> **SPEC** — Watch for engulfing bars and inside-bar breaks. Mark each pattern on the candle it
> completed on, labelled with its name, and draw the trigger level across the chart — the high of the
> signal bar for a long, the low for a short — until price takes it or the setup expires. Enter on
> the break of that level, stop at the other end of the signal bar, target twice the risk, and show
> the stop and the target as labelled lines while a position is open.

**Accept when** the pattern bar is visually distinguished from its neighbours · the trigger, stop and
target are labelled lines rather than bare marks · a setup that expires stops being drawn.

**Watch for** a marker at the close of a bar the pattern did not complete on, and stop/target lines
that persist after the position closed.

---

## S3 · Opening range breakout

**Category** session structure · **Data** bars · **Test on** ES or SPY, 1-minute
**Reference** [ORB in the strategy survey](https://www.quantifiedstrategies.com/day-trading-strategies/)

**Visual target.** The session's first N minutes shaded as a box with its high and low extended
across the rest of the day as two horizontal rails; the breakout bar marked; the day's other sessions
visibly separated.

> **ASK** — `an opening range breakout strategy`

> **SPEC** — Take the high and low of the first fifteen minutes of the session, draw that range as a
> shaded box, and extend its two edges across the rest of the day. Go long when a bar closes above
> the high and short when one closes below the low, once per side per day, with the stop at the
> opposite edge. Shade the box in a neutral colour, colour the two rails by which side has broken,
> and show the range width, the time left in the opening period and whether each side has already
> fired.

**Accept when** the range is computed from the session open and not from the buffer's first bar ·
the box is drawn once and extended, not redrawn every bar · the once-per-side rule holds across a
day boundary.

**Watch for** the session boundary being wrong or absent — this brief exists partly to find out
whether a unit can express one at all.

---

## S4 · Anchored VWAP bands

**Category** institutional execution · **Data** bars + trade tape · **Test on** ES or SPY
**Reference** [VWAP as the professional's fair-value line](https://www.quantifiedstrategies.com/day-trading-strategies/)

**Visual target.** Price with a heavy VWAP line anchored to the session open, one or two standard
deviation bands shaded either side of it, and the distance from VWAP shown as a signed number that
changes colour with its sign.

> **ASK** — `a VWAP mean reversion strategy`

> **SPEC** — Anchor a volume-weighted average price at the session open and keep it running. Draw it
> heavier than the price line, shade one and two standard deviations either side, and fade moves that
> reach the outer band back toward the average — long below, short above — flattening at the VWAP
> itself. Show the current VWAP, how far price is from it in both points and standard deviations, and
> how much volume has gone into it today.

**Accept when** the average is volume-weighted rather than a simple mean · the anchor resets at the
session and not at the first bar in memory · the bands widen and narrow with realised dispersion
instead of being fixed offsets.

**Watch for** a moving average relabelled as VWAP. That failure looks completely right on screen and
is the reason the deviation readout is part of the accept list.

---

## S5 · Smart money / ICT

**Category** institutional structure · **Data** bars · **Test on** ES or BTCUSDT, 5-minute
**Reference** [ICT strategy guide](https://www.quantum-algo.com/blog/ict-trading-strategy-complete-guide/)

**Visual target.** Boxes on the chart: fair-value gaps as thin rectangles spanning the untraded
range, order blocks as thicker ones at the origin candle, each fading once price returns into it.
Labels where structure breaks. A liquidity sweep marked at the wick that took out the prior high or
low.

> **ASK** — `an ICT smart money concepts strategy`

> **SPEC** — Mark the three things this method is built on. A fair-value gap is the untraded space
> left when a bar's low is above the high of the bar two back; draw it as a rectangle that stays
> until price trades back into it, then fades. An order block is the last opposing candle before a
> move that breaks structure; draw it as a zone and label it. A liquidity sweep is a wick through a
> prior swing high or low that closes back inside; mark it. Enter when price returns into an order
> block after a sweep in the same direction, stop beyond the sweep's wick, target the opposite
> liquidity. Label breaks of structure as they happen.

**Accept when** the gap is measured across three bars and not two · a zone is removed or faded once
it has been traded through · the swing high and low used for the sweep are real pivots and not just
the running maximum.

**Watch for** boxes that accumulate forever. Fifty stale zones is the standard failure of this
picture and it is a memory problem as well as a visual one.

---

## S6 · Queue imbalance scalper

**Category** order book · **Data** depth + L1 · **Test on** BTCUSDT or ES
**Reference** [Queue imbalance as a one-tick-ahead predictor (arXiv 1512.03492)](https://arxiv.org/pdf/1512.03492) ·
[Latency and order-book imbalance strategies (arXiv 2006.08682)](https://arxiv.org/pdf/2006.08682)

**Visual target.** A price ladder with resting size on both sides drawn as proportional bars, the
best bid and ask highlighted, and a signed imbalance meter beside it that fills toward whichever side
is heavier. The recent path of that imbalance drawn as a small line so the trend in it is visible.

> **ASK** — `an order book imbalance scalping strategy`

> **SPEC** — Read the resting size at the touch and a few levels behind it, and turn the two sides
> into one number between minus one and plus one. Take a long when it is strongly positive and
> persists for a moment rather than a single snapshot, exit on it decaying or reversing, mirror for
> shorts. Show the ladder with both sides as proportional bars and the touch highlighted, a meter for
> the current imbalance, and the last minute of it as a line so I can see whether it is building or
> fading. Cap the hold time.

**Accept when** the imbalance is a ratio of sizes and stays inside its bounds · a single snapshot
does not fire a trade · the ladder is anchored on the touch rather than a fixed price grid.

**Watch for** a depth requirement not being declared, which makes the whole picture empty and every
other axis unjudgeable.

---

## S7 · Stacked-imbalance absorption

**Category** volume footprint · **Data** trade tape · **Test on** ES or BTCUSDT
**Reference** [NinjaTrader Order Flow + volumetric bars](https://ninjatrader.com/trading-platform/free-trading-charts/order-flow-trading/) ·
[imbalance detection and gradient shading](https://docs.ninjatrader.com/ninjascript/order_flow_volumetric_bars) ·
[ATAS footprint and stacked imbalance](https://atas.net/volume-analysis/footprint-charts/)

**Visual target.** Footprint bars: each time bar a column of price cells, bid volume against ask
volume side by side in each cell, imbalanced cells tinted, the point of control marked per bar, and
consecutive imbalances at adjacent prices highlighted as a stack.

> **ASK** — `a footprint imbalance strategy`

> **SPEC** — Inside each bar, split the traded volume by price into bid against ask. Compare each
> price diagonally against the one below it, and call it imbalanced when one side is several times
> the other. Three or more of those stacked at consecutive prices is the signal: at the low of a bar
> it is buying absorption, at the high it is selling. Trade in the direction of the stack when the
> next bar holds above it, stop below the stack. Draw the footprint with the imbalanced cells tinted
> and the stacks outlined, mark the point of control on each bar, and show the session delta.

**Accept when** the comparison is diagonal, not level-against-level in the same row · a stack is
three or more consecutive · delta is signed buy-minus-sell and not total volume.

**Watch for** a horizontal comparison. It is the single most common way this indicator is written
wrongly, it produces a plausible picture, and nothing downstream catches it.

---

## S8 · Order-flow toxicity gate

**Category** microstructure · **Data** trade tape + L1 · **Test on** BTCUSDT or ES
**Reference** [VPIN and real-time order toxicity](https://www.visualhft.com/blog/vpin-real-time-order-toxicity-what-your-execution-stack-cannot-see/) ·
[Easley, López de Prado & O'Hara, flow toxicity in a high-frequency world](https://www.stern.nyu.edu/sites/default/files/assets/documents/con_035928.pdf)
**In-repo control** the SigmaIcFlow brief in `.claude/context/tasks/hyperion-window-briefs.md`.

**Visual target.** A gauge for the toxicity measure with its own scale, a line of the signed
order-flow imbalance over time beneath it, and a small table of each component with its current value
— the picture a market maker watches to decide whether to keep quoting.

> **ASK** — `a strategy that stops trading when order flow gets toxic`

> **SPEC** — Measure how one-sided the flow is: bucket trades by volume rather than by time, and
> score each bucket by how far from balanced its buy and sell volume was. Run a signed order-flow
> imbalance beside it and a price-impact slope. Trade a simple flow-following rule, but suspend it
> whenever the toxicity measure is in its top decile, and say so on screen. Show the toxicity as a
> gauge, the imbalance as a line over the last few minutes, and a table of every component with its
> value and whether it is currently blocking.

**Accept when** the buckets are volume-clocked and not time-clocked · trades are classified into
buy and sell by a real rule rather than assumed · the gate visibly says why it is blocking.

**Watch for** time bucketing. It is the defining property of the measure and the easy thing to get
wrong.

---

## S9 · Liquidity wall absorption

**Category** heatmap / liquidity · **Data** depth · **Test on** BTCUSDT
**Reference** [Bookmap heatmap overview](https://bookmap.com/en/learning-center/getting-started/liquidity-heatmap/heatmap-overview) ·
[what the heatmap reveals](https://bookmap.com/blog/heatmap-in-trading-the-complete-guide-to-market-depth-visualization)
**In-repo control** the ImbalanceHeatFront brief.

**Visual target.** Time on the horizontal axis, price on the vertical, each cell shaded by how much
size has been resting there — so a large persistent order is a bright horizontal streak. Trades drawn
as dots at their price, sized by volume. The wall being eaten into is the whole story.

> **ASK** — `a strategy that trades absorption at big resting orders`

> **SPEC** — Keep a history of the resting size at each price so I can see where liquidity has been
> sitting, drawn as a heatmap with time across and price up, brighter where more size rested. Find
> the largest resting order within a few ticks of the touch and watch it: if price reaches it and it
> holds while trades keep hitting it, that is absorption and I want to fade toward it; if it
> disappears before price arrives it was never real and I want nothing. Draw trades as dots at their
> price sized by volume, mark the wall being watched, and show its size, how much has been eaten and
> how long it has stood.

**Accept when** the heatmap is a history and not a snapshot of the current book · a pulled order stops
being drawn as if it were still there · trades are placed at their own price rather than the mid.

**Watch for** an unbounded history buffer. This picture is where that leak has appeared before.

---

## S10 · Value-area breakout

**Category** market profile · **Data** trade tape or bars · **Test on** ES
**Reference** [Sierra Chart TPO profile charts](https://www.sierrachart.com/index.php?page=doc%2FStudiesReference%2FTimePriceOpportunityCharts.html)

**Visual target.** A horizontal volume-at-price profile down the side of the chart, the point of
control drawn across, the value area shaded, and the first hour's range marked separately as the
initial balance.

> **ASK** — `a market profile value area strategy`

> **SPEC** — Build the session's profile of volume traded at each price and draw it as a horizontal
> histogram beside the chart. Mark the busiest price, and shade the band around it that contains
> seventy percent of the session's volume. Mark the first hour's high and low separately. Trade the
> edges: fade a probe outside the value area that fails to hold, and go with a break of the initial
> balance that does. Show the value area high and low, the point of control and the initial balance
> range as numbers.

**Accept when** the value area really is 70 % of volume and grows toward the busier neighbour rather
than symmetrically · the initial balance is the first hour of the session, not of the buffer · the
profile resets daily.

**Watch for** a value area centred on the point of control. That is the intuitive implementation and
it is wrong; the correct one is asymmetric.

---

## Not here, and why

**Pairs / statistical arbitrage.** `SandboxStrategyRuntime.ResolveInstruments` accepts exactly one
resolved instrument parameter and throws otherwise, so a spread strategy cannot start. It is the
first brief to add when that lifts, and it is worth writing the day it does: a z-score of a hedged
spread is the standard picture and none of the ten above exercises two instruments.
