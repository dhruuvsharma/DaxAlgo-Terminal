# Order Book

`TradingTerminal.OrderBook` — **Charts → Order book…**

> Live L2 window for one instrument, four analytics layers deep: the classic **depth ladder**
> (asks stacked above the spread, bids below, size + cumulative bars), a **microstructure strip**
> (microprice, weighted mid, L1 + cumulative queue imbalance, book skew, sweep costs), a scrolling
> **liquidity heatmap** (time × price, resting size → intensity, best-bid/ask + microprice lines,
> aggressor-coloured trade dots), an **imbalance lane** with an OLS pressure-trend line — and an
> **ML micro-forecaster**: an online-learned model that predicts the microprice path 250 ms–5 s
> ahead and the probability of spread-widening / depth-drain / sweep-cost-jump events, scored live
> against the naive queue-imbalance rule. **Display only.**

## Data requirements

| L1 | Bars | Depth | Trade tape |
|:--:|:--:|:--:|:--:|
| — | — | ✅ | optional (synthetic depth-mid fallback) |

Depth-capable backends today: **cTrader**, **Ironbeam**, **Upstox**, **Binance**, **Coinbase**,
**Bybit**, **Kraken**, **OKX**, **Simulated**. Alpaca, NinjaTrader and LSE don't serve L2; IB's
`reqMktDepth` is not wired in this build. A broker without depth leaves the window empty with the
reason in the status line. Trade tape (for the flow overlays and the ML flow features): native on
IB / Binance / Ironbeam, synthesized from depth-mid ticks elsewhere.

## Settings

| Setting | Values | What it does |
|---|---|---|
| Instrument | searchable picker | subscribes depth (+ tape when available) via `IMarketDataIngest` |
| Sweep size | 100 / 500 / 1k / 5k / 10k | the size the sweep-cost read-outs (and ML sweep features) price |
| Heatmap / Trades / Microprice / Imbalance lane | checkboxes | view layers — redraw only, never restart |
| ML forecast | checkbox, default on | shows/hides the violet forecast path + probability chips |
| ML warm-start | checkbox, default on | pre-train the model from stored depth on the next (re)start |

## The heatmap

Captured once per **250 ms** tick (decoupled from the depth rate): each column is one snapshot of
the visible book, teal = bid liquidity, red = ask, alpha ∝ resting size; best-bid/ask lines,
dashed microprice line, and trade dots (green buy / red sell, radius ∝ size) ride on top. The ring
keeps 600 columns (~2.5 min). The bottom lane plots per-column cumulative imbalance bars with the
OLS trend line from the VM.

## Microstructure strip

| Value | Definition |
|---|---|
| Bid / Ask / Spread / Mid | top of book from the latest snapshot |
| Microprice | size-weighted L1 fair price — leans toward the thinner side |
| Wgt mid | size-weighted mid across the top 10 levels |
| Imbalance | cumulative top-10 queue imbalance in [−1, 1], colour by sign |
| Imb trend | OLS slope of cumulative imbalance per column |
| Book skew | total bid depth ÷ total ask depth (top 10) |
| Buy / Sell sweep | slippage to fill the sweep size through that side (⚠ = book too thin) |
| CVD | cumulative delta from the (real or synthetic) tape |
| P SPREAD↑ / P DEPTH↓ / P SWEEP↑ 2s | ML event probabilities (below) with mini-bars; amber ≥ 40%, red ≥ 70% |
| ML 1s / OBI 1s (hit·MAE) | the scoreboard: rolling 1 s directional hit-rate + MAE (ticks) for the model vs the queue-imbalance rule — ML row green when it's winning |
| ML samples | steps the model has trained on (warm-start included) |

## ML micro-forecaster — the math

### In plain terms

Every 250 ms the window takes one "step": it summarises the book (imbalances, depth, spread,
gaps, sweep costs) and the trade flow since the last step, asks the model for a forecast, and —
crucially — first *grades* the forecasts whose time has come due against what actually happened,
teaching the model from every miss. There is no training file and no setup; the model earns (or
loses) trust live, and the scoreboard shows it head-to-head against the classic rule of thumb
("more depth on the bid ⇒ price ticks up").

### The model

`Core/Ml/OrderBookMicroPredictor` — a bank of **recursive-least-squares** learners
(`OnlineLinearRegression`, forgetting λ = 0.995):

- **Direction**: 5 learners, one per **direct horizon** h ∈ {1, 2, 4, 8, 20} steps (250 ms → 5 s),
  each fitting the microprice change in ticks, $y_h = (\mu_{t+h} - \mu_t)/\tau$. Direct horizons
  — not an iterated 1-step model — so no compounding and no invented future feature vectors; that
  is also why the forecast path stops at 5 s.
- **Events**: 3 learners over one W = 8-step (2 s) window, linear probability models (outputs
  clamped to [0, 1]) for labels defined in `OrderBookEventLabeler`:
  spread widened (max spread over W ≥ reference + **1 tick**), depth drained (**either side's**
  top-3 depth dipped ≤ 70% of reference), sweep jumped (worst-side sweep cost ≥ 1.25× reference,
  one-tick floor).
- **Features** (19 dims, price terms in ticks, standardized online by `OnlineFeatureScaler` with
  ±5 clipping, bias passthrough): three lagged microprice changes; L1 + cumulative imbalance;
  microprice−mid offset; spread; log-depth skew; relative total depth vs EWMA; per-side top-3
  depth deltas; largest level gap; worst sweep cost + sweep asymmetry; normalized signed flow +
  its EWMA; trade intensity; and a flow-validity flag (warm-start steps have no tape — the model
  learns to discount flow features when the flag is 0).
- **Tick size is estimated internally** (running minimum of positive adjacent level gaps, default
  0.01 until observed); each pending forecast pins the tick it was created with, so refinement
  never corrupts queued targets.
- **Scoring** — ex-ante by construction: the flagship 1 s (h = 4) forecast is graded by rolling
  MAE (ticks) + directional hit-rate (`RollingForecastMetrics`, window 200) against the
  **queue-imbalance baseline** — predicted move = sign(L1 imbalance) × half-spread, dead-band
  |imb| < 0.05 — on identical realized steps. The rule's hit-rate is the meaningful column (it
  only calls direction); MAE is shown for completeness. Event probabilities are graded by rolling
  **Brier score** next to the observed base rate (`RollingBrierScore`) — a useful model beats
  "always predict the base rate", i.e. Brier < r(1−r).
- **Warm-start**: on (re)start the VM replays up to 30 min of stored depth
  (`IMarketDataStore.ReadDepthAsync`, all brokers merged) through `DepthStepSampler` — LOCF
  resampling onto the 250 ms grid, recording gaps > 5 s skipped — training the model off the UI
  thread before it goes live; a watermark prevents re-learning the seam. Stores without depth
  persistence simply yield a cold start. Readiness needs 40 flagship updates (~10 s live).

### Rendering

The forecast draws in a **right gutter of the heatmap** (violet `#B388FF`, dotted — the app's ML
visual identity, matching the footprint's ML ghosts): a faint wash + dotted divider mark "now",
then a dotted polyline runs from the last live microprice through the predicted microprice at
each horizon (one column = one step), dots at the horizon points. Event probabilities are chips
in the strip, not canvas.

**Caveat:** linear in its 19 engineered features — it adapts (forgetting) and self-reports (the
scoreboard), but it is a lightweight online model, not a deep net. A green ML row means "the book
features currently carry short-horizon signal here", not a trading system.

## Code map

| What | Where |
|---|---|
| VM (streams, strip, heatmap capture, ML step + warm-start) | `OrderBookViewModel.cs` |
| Heatmap + forecast renderer | `OrderBookWindow.xaml.cs` |
| Ladder / strip / chips UI | `OrderBookWindow.xaml` |
| Microstructure math | `Core/MarketData/Microstructure.cs` |
| ML engine (RLS bank, features, targets) | `Core/Ml/OrderBookMicroPredictor.cs` (tests: `tests/…/Ml/OrderBookMicroPredictorTests.cs`) |
| Event labels / Brier / warm-start resampler | `Core/Ml/OrderBookEventLabeler.cs` · `RollingBrierScore.cs` · `DepthStepSampler.cs` (each with tests) |
| Depth seam | `IBrokerClient.SubscribeDepthAsync` → ingest → `IMarketDataHub.Depth(InstrumentId)` |
