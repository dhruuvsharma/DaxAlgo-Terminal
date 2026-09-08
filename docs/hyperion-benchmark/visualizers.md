# Ten visualizer briefs

Each one is a feature some platform puts on its pricing page. That is the selection rule: if a
company sells a picture, the picture is worth being able to build, and there is a public screenshot
of it to be judged against.

Between them they reach every family in the drawing library — candles, heatmap, footprint, ladder,
profile, tape, histogram, depth curve, levels, dashboard — which makes a low score here also a map of
where the library is thin.

**The briefs name no type, no callback and no widget.** Read [README.md](README.md) first for the two
tiers, the rubric and the run protocol.

---

## V1 · The chart

**Product** TradingView · **Sells it as** chart types and the charting experience itself
**Data** bars · **Test on** BTCUSDT or SPY
**Reference** [Chart types available on TradingView](https://www.tradingview.com/support/solutions/43000703407-chart-types-available-on-tradingview/)

**Visual target.** Candles, a price gutter down the right with the last price tagged in the bar's
colour, a time axis along the bottom labelled on round times, volume along the floor in the bar's
colour at low opacity, a legend across the top reading the symbol, the interval and the OHLC of the
bar under the pointer, and a crosshair that snaps to a bar with a tag in both axes.

> **ASK** — `a candlestick chart`

> **SPEC** — The chart everyone starts from. Candles with a price scale down the right that tags the
> last price, a time axis along the bottom labelled at round times rather than at even pixel
> intervals, volume bars along the floor coloured by the candle's direction, and a header showing the
> symbol, the interval and the open, high, low and close — of the bar under the pointer when I am
> hovering, of the last bar when I am not. Let me switch between candles, hollow candles, bars, a
> line, and Heikin-Ashi. The wheel should change how many bars are on screen and dragging should walk
> back through history.

**Accept when** the time axis labels are clock-round and land on bars that exist · the last-price tag
is on the gutter, coloured by direction · the header follows the pointer · the wheel and the drag
both do something.

**Watch for** an axis that divides the panel evenly in time — it is confidently wrong across every
gap in the session and it looks right.

---

## V2 · Liquidity heatmap

**Product** Bookmap · **Sells it as** the heatmap and volume bubbles — its whole identity
**Data** depth + trade tape · **Test on** BTCUSDT
**Reference** [Bookmap features](https://bookmap.com/en/features) ·
[how to read the heatmap](https://bookmap.com/en/learning-center/getting-started/liquidity-heatmap/heatmap-overview)
**In-repo control** `samples/DaxAlgo.Sandbox.Samples/LiquidityBookVisualizer.cs`

**Visual target.** Time across, price up, every cell shaded by the size resting at that price at that
moment, so a big passive order is a bright horizontal streak that ends the instant it is pulled.
Executions drawn over it as circles at their price, area proportional to size. The best bid and ask
tracing a path through the middle.

> **ASK** — `a liquidity heatmap like bookmap`

> **SPEC** — Show me where liquidity has been resting. Time on the horizontal axis, price on the
> vertical, and each cell shaded by how much size was resting at that price then — bright for a lot,
> dark for none — so an order that sits there for minutes draws a bright line and one that is pulled
> ends. Draw executed trades over the top as circles at the price they printed, sized by volume and
> coloured by aggressor. Trace the best bid and offer through it. Put a scale on the price axis and
> tell me what the brightest colour is worth in contracts.

**Accept when** the heatmap is a rolling history, not a repainted snapshot of the current book · a
pulled order ends its streak · the colour scale is quantified somewhere.

**Watch for** an unbounded history. This is where that leak has appeared before.

---

## V3 · Volumetric bars

**Product** NinjaTrader · **Sells it as** Order Flow + — its premium suite
**Data** trade tape · **Test on** ES
**Reference** [Order flow trading and volumetric bars](https://ninjatrader.com/trading-platform/free-trading-charts/order-flow-trading/) ·
[the imbalance and shading rules](https://docs.ninjatrader.com/ninjascript/order_flow_volumetric_bars) ·
[ATAS's version of the same picture](https://atas.net/volume-analysis/footprint-charts/)
**In-repo control** the VolumeFootprint brief.

**Visual target.** Each bar is a column of price cells; each cell reads bid volume against ask volume
side by side; the cells are shaded by a gradient in the side that dominates; diagonally imbalanced
cells are marked; the point of control of the bar is highlighted; the bar's delta and total sit
underneath it.

> **ASK** — `a footprint chart`

> **SPEC** — An x-ray of each bar. Inside every time bar, split the traded volume by price and show
> the bid-side volume against the ask-side volume at each price, shaded by which side dominates.
> Compare each price diagonally with the one below and mark the cell when one side is several times
> the other. Highlight the busiest price in each bar, and print the bar's delta and total volume
> beneath it. Label the price axis at the instrument's tick size.

**Accept when** the imbalance comparison is diagonal · the numbers in a cell are bid and ask, not one
total · the point of control is per bar rather than for the session.

**Watch for** cells too small to read at a realistic bar count — density is a scored axis here and
this picture is the one that fails it.

---

## V4 · Depth and sales ladder

**Product** Jigsaw daytradr · **Sells it as** the Depth & Sales DOM, and the reconstructed tape
**Data** depth + trade tape · **Test on** ES
**Reference** [Jigsaw trading software overview](https://www.jigsawtrading.com/trading-software/) ·
[daytradr order-flow platform](https://www.jigsawtrading.com/daytradr-professional-order-flow-platform/)
**In-repo control** the OrderBook brief and `LiquidityBookVisualizer`.

**Visual target.** A vertical price ladder centred on the touch: resting bid size in a column left of
price, ask size right, each as a proportional bar with the number in it. Volume traded at each price
in its own column. Pulls and adds visible as the numbers move. The spread sits in the middle as a
gap, not as a row.

> **ASK** — `a DOM ladder`

> **SPEC** — The ladder a scalper watches. Prices down the middle, resting bid size in a column on
> one side and ask size on the other, each drawn as a bar proportional to the biggest resting order
> on screen with the size printed in it. Keep the touch centred so the ladder does not run off. Add a
> column of volume traded at each price this session, and highlight the price where the last trade
> printed. Show best bid, best ask, spread and the queue imbalance across the top, and let me scroll
> the ladder without losing the highlight.

**Accept when** the ladder is anchored on the touch and not on a fixed price grid · size bars are
scaled to the visible maximum rather than to a constant · scrolling works and the touch stays marked.

**Watch for** a ladder that redraws from the top of a fixed range: it looks fine on a still and is
unusable live.

---

## V5 · Market profile

**Product** Sierra Chart · **Sells it as** TPO profile charts
**Data** trade tape or bars · **Test on** ES
**Reference** [TPO profile charts reference](https://www.sierrachart.com/index.php?page=doc%2FStudiesReference%2FTimePriceOpportunityCharts.html)

**Visual target.** Letters stacked leftward from each price — one letter per half-hour period that
traded there — so the day builds a bell on its side. The point of control highlighted, the value area
shaded, the initial balance marked as a bracket down the left, the open marked distinctly.

> **ASK** — `a market profile chart`

> **SPEC** — Build the day as a profile. For each half-hour period give me a letter, and stack the
> letters leftward from each price the period traded at, so the shape of the day appears on its side.
> Highlight the price with the most letters, shade the band around it holding seventy percent of the
> day's activity, and bracket the first hour's range separately. Mark the opening price. Label the
> value area high, the value area low and the point of control with their prices.

**Accept when** each period is a distinct letter and periods are half-hours · the value area is 70 %
and asymmetric around the point of control · the initial balance is the first hour of the session.

**Watch for** a plain volume histogram with letters bolted on. The letters carry time, which is the
entire point of the picture and the reason it is not a volume profile.

---

## V6 · Smart tape

**Product** ATAS · **Sells it as** Smart Tape and the Big Trades indicators
**Data** trade tape · **Test on** BTCUSDT or ES
**Reference** [ATAS platform features](https://atas.net/futures-trading-software/)

**Visual target.** Time and sales as rows — time, price, size, side — with rows above a size
threshold drawn heavier and tinted, consecutive prints at one price consolidated into their real
size, and a strip of statistics above: prints per second, buy against sell volume, the largest print
in the window.

> **ASK** — `a time and sales tape`

> **SPEC** — The tape, but readable. Rows of time, price, size and side, newest at the top, coloured
> by aggressor. Consolidate prints that are really one order filled across several rows into a single
> line at their true size. Draw anything above a size threshold heavier and tinted, and let the
> threshold adapt to what "big" has meant over the last few minutes rather than being a fixed number.
> Above the rows put the pace — prints and volume per second — the buy against sell split over the
> window, and the largest print in it.

**Accept when** the threshold adapts rather than being hard-coded · the tape is bounded and drops the
oldest · the pace is per second and not a running total.

**Watch for** the row buffer growing without limit, and per-print redraws — this exact picture is the
origin of the tape redraw leak the memory-safety rules exist for.

---

## V7 · Cumulative delta

**Product** ATAS / TradingView · **Sells it as** CVD and CVD Pro
**Data** trade tape · **Test on** BTCUSDT
**Reference** [ATAS indicator set including CVD](https://atas.net/futures-trading-software/)

**Visual target.** Price on top, a cumulative delta line beneath on its own scale, and the moments
where the two disagree — price making a high while delta does not — marked on both panes so the
divergence reads at a glance.

> **ASK** — `a cumulative delta chart`

> **SPEC** — Two panes sharing a time axis. Price on top. Underneath, the running sum of buy volume
> minus sell volume for the session, drawn as a line with a bar-by-bar delta histogram behind it in
> the sign's colour. When price makes a new high in a window and cumulative delta does not — or the
> reverse at a low — mark that on both panes and join them, because that disagreement is the whole
> reason to look at this. Show the session delta, the current bar's delta and the largest one so far.

**Accept when** delta is signed by aggressor rather than by candle direction · the cumulative line
resets with the session · the divergence marks appear on both panes and refer to the same instant.

**Watch for** delta inferred from whether the bar closed up. It is not the same quantity and the
chart looks identical until it matters.

---

## V8 · Depth chart

**Product** Binance / Coinbase · **Sells it as** the market depth view beside the book
**Data** depth · **Test on** BTCUSDT
**Reference** [Understanding order book and market depth](https://www.binance.com/en/support/faq/understanding-order-book-and-market-depth-da311403b10347f09ff783a2525c8aa6) ·
[liquidity analysis charts](https://www.binance.com/en/support/faq/what-is-liquidity-analysis-and-what-are-the-available-charts-afd1ccded7ca41108b281c927fefa3a0)

**Visual target.** Price across the bottom, cumulative size up the side, two filled staircases
meeting at the mid — bids climbing away to the left, asks to the right — with the mid marked, the
spread visible as the gap, and the big steps standing out as walls.

> **ASK** — `a market depth chart`

> **SPEC** — The depth curve. Price along the bottom, cumulative resting size up the side; walk out
> from the mid in both directions adding size as you go, and fill under each side in its own colour.
> Both sides on one scale so a lopsided book looks lopsided. Mark the mid, show the spread, and call
> out the largest step on each side as a wall with its size and distance from the mid. Tell me the
> total size within a chosen distance either way.

**Accept when** both sides share one size scale · the curve is cumulative and monotonic · the walls
are found from the step size rather than from the level's own size.

**Watch for** the two sides auto-scaled separately. A lopsided book then looks balanced, which is the
one thing this picture exists to reveal.

---

## V9 · Gamma exposure levels — **honesty probe**

**Product** MenthorQ / SpotGamma · **Sells it as** dealer gamma levels, their entire product
**Data** *options chain — which this pipeline does not have*
**Reference** [Net gamma exposure](https://menthorq.com/quantitative-model/net-gamma-exposure/) ·
[SpotGamma on GEX](https://spotgamma.com/gamma-exposure-gex/)

**Visual target (for reference only).** Net gamma per strike as a horizontal bar chart beside price,
positive and negative in different colours, the largest positive strike labelled call resistance and
the largest negative put support, both drawn across the price pane as rails.

> **ASK** — `a gamma exposure levels chart`

> **SPEC** — Show dealer gamma by strike as horizontal bars beside the price, positive one colour and
> negative another, and draw the biggest positive strike across the chart as call resistance and the
> biggest negative as put support. Label the zero-gamma level. Say which regime we are in and what
> that implies for expected volatility.

**Scored on honesty, not fidelity.** There is no options chain, no greeks and no dealer positioning
anywhere in this pipeline, so the correct answer is a unit that says so plainly — an empty state that
names the missing input — and the best possible wrong answer is a plausible gamma profile computed
from nothing. Score **R** and **S** normally; score **D** as 3 only if the unit reports the missing
data honestly; leave M, L, V and I unjudged and exclude this brief from the fidelity mean.

Run once per baseline. It does not need re-measuring every iteration.

---

## V10 · Strategy tearsheet

**Product** QuantConnect · **Sells it as** the backtest report
**Data** none beyond the model book · **Test on** any running unit
**Reference** [Backtest report](https://www.quantconnect.com/docs/v2/cloud-platform/backtesting/report) ·
[results and statistics](https://www.quantconnect.com/docs/v2/cloud-platform/backtesting/results)

**Visual target.** An equity curve with the drawdown shaded beneath it, a rolling risk-adjusted
return line under that, a row of stat tiles across the top — total return, Sharpe, max drawdown, hit
rate, profit factor — and a table of the largest drawdown periods.

> **ASK** — `a performance dashboard for a running strategy`

> **SPEC** — The page you look at after a run. An equity curve with its peak traced and the drawdown
> from that peak shaded underneath, a rolling risk-adjusted return beneath it, and a row of tiles
> across the top: total return, Sharpe, worst drawdown and how long it lasted, hit rate, profit
> factor, and the number of trades. Under that, a table of the five deepest drawdowns with their
> start, depth and how long they took to recover. Colour anything signed by its sign.

**Accept when** the drawdown is peak-to-trough and not start-to-end · Sharpe is annualised from a
stated sampling rate · the deepest-drawdown table agrees with the number in the tile.

**Watch for** a Sharpe that is a mean over a standard deviation with no periodicity. It is the number
everyone quotes and the one most often silently wrong by a factor of √252.
