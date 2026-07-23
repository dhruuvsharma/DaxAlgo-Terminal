# Charts & order-flow windows

> Last updated: 2026-07-03

### In plain terms

The **Charts** menu holds five ways to *look* at a market, from familiar to exotic:

- **Charts** — an ordinary candlestick price chart with moving-average / RSI / MACD overlays. If
  you've used any trading app, this is the one you already know. *(Windows build only.)*
- **Order book** — the live queue of resting buy and sell orders ("Level 2 / depth"): who's waiting to
  buy or sell, at what price, and in what size — plus a learned micro-forecast of where the fair
  price is headed in the next few seconds.
- **Volume footprint** — instead of just price, it shows *how much* traded at *each price* inside each
  bar, split into aggressive buying vs selling — so you can see where the real activity happened,
  with regression and ML predictors extending the picture forward.
- **Bookmap + VolBook** — the "x-ray" view: a colour heatmap of where liquidity is sitting in the
  order book over time, with every trade printed on top, plus a volume profile and a running
  buy-minus-sell line. It's the most information-dense window in the app.
- **3D Surface lab** — the researcher's window: bucket an instrument's returns by two dimensions
  (hour × weekday, prior return × volatility, …) and fly around the resulting 3D statistic
  surface — with a robustness overlay that tells you which peaks are real and which are noise.

You don't need to read all five; pick the one that matches what you're trying to see. The rest of this
page is the precise reference.

Reference for every window under the **Charts** menu: what it shows, what it needs, and every input/parameter it exposes. Each opens as its own window, streams through the canonical pipeline (`IMarketDataHub` / `IMarketDataIngest` — never a broker SDK directly), and uses the shared instrument picker (one global catalog; see [user-guide.md](user-guide.md#8-customisations)).

> 📐 **The math** behind the footprint imbalance rule, regression fits, heatmap √-compression, VWAP, CVD, and value area is collected in the [Methods & math reference](math-reference.md#4-chart-math).

## Data requirements at a glance

| Window | Needs | Served by |
|---|---|---|
| Charts | historical bars + L1 | all brokers |
| Order book | L2 depth | cTrader, Binance, Ironbeam, Upstox, crypto venues (Coinbase/Bybit/Kraken/OKX), Simulated |
| Volume footprint | trade tape (synthetic L1 fallback) | tape: IB, Binance, Ironbeam, crypto venues, Simulated; fallback: everything else |
| Bookmap + VolBook — heatmap / DOM | L2 depth | cTrader, Binance, Ironbeam, Upstox, crypto venues, Simulated |
| Bookmap + VolBook — volume profile / VWAP / CVD / dots | trade tape | IB, Binance, Ironbeam, crypto venues, Simulated |
| 3D Surface lab | historical bars (live mode adds L1 + optional tape) | all brokers |

A window opened against a broker that can't serve its feed reports it in the status line (footprint falls back to a synthetic tape; Bookmap's volume features stay empty until a tape is present). **Note:** IB depth (`reqMktDepth`) is not yet wired — IB serves L1 + tape but not the L2 order book.

## Charts (TradingView-style)

> **Windows build only.** This window renders through WebView2 (an embedded browser), which is a
> Windows component — so it ships only in the WPF tree. The other three Charts windows run on both
> builds.

**Charts → Charts…** — candlestick charting rendered by Lightweight Charts inside a WebView2; all numbers are computed in C# (`Core` indicators), so chart, backtest, and live values agree.

Loads history from `IMarketDataRepository`, then streams the forming candle live from the hub.

> 🖼️ _Screenshot — coming soon_
> 🎬 _Video walkthrough — coming soon_

| Input | Values | Notes |
|---|---|---|
| Instrument | searchable picker | broker universe + catalog |
| Timeframe | 1m / 5m / 15m / 1h / 1D | default 1h |
| SMA | on/off (default on) | period 20 |
| EMA | on/off (default on) | period 50 |
| RSI | on/off (default off) | period 14, separate pane |
| MACD | on/off (default off) | 12 / 26 / 9, separate pane |

History lookback is fixed per timeframe: 1m → 2 days, 5m → 5 days, 15m → 15 days, 1h → 60 days, 1D → 365 days.

## Order book

**Charts → Order book…** — the live L2 window for one instrument, four layers deep: the classic **depth ladder** (asks above the spread, bids below, size + cumulative bars normalized to the biggest level on either side), a **microstructure strip** (microprice, weighted mid, L1 + cumulative queue imbalance, book skew, cost-to-fill sweep read-outs for a chosen size), a scrolling **liquidity heatmap** (time × price, teal bid / red ask intensity, best-bid/ask + microprice lines, aggressor-coloured trade dots), and a bottom **imbalance lane** with an OLS pressure-trend line. The heatmap captures one column per 250 ms, decoupled from the book's update rate.

> 🖼️ _Screenshot — coming soon_
> 🎬 _Video walkthrough — coming soon_

| Input | Values |
|---|---|
| Instrument | searchable picker (depth-capable broker required) |
| Sweep size | 100–10k — the size the sweep-cost read-outs price |
| View toggles | Heatmap · Trades · Microprice · Imbalance lane · ML forecast · ML warm-start |

Trade tape is native on IB / Binance / Ironbeam and synthesized from depth-mid ticks elsewhere, so the flow overlays always show something.

### ML micro-forecast

The learned layer (violet, like the footprint's ML ghosts): an **online model that trains itself on the live book** — every 250 ms step it grades the forecasts that just came due against what actually happened, learns from the miss, and forecasts again. No training files; warm-starts from stored L2 history so it's usually ready immediately.

- **Forecast path**: a violet dotted line ahead of the heatmap's "now" divider — the predicted **microprice path** at 250 ms / 500 ms / 1 s / 2 s / 5 s, drawn in a right gutter at the same column scale as the heatmap.
- **Event gauges** in the strip: **P SPREAD↑ / P DEPTH↓ / P SWEEP↑** — the probability, over the next 2 s, that the spread widens ≥ 1 tick, either side's top-3 depth drains ≥ 30%, or the sweep cost jumps ≥ 25%. Chips turn amber ≥ 40% and red ≥ 70% — an early-warning row for liquidity behavior.
- **Scoreboard**: **ML 1s** vs **OBI 1s** — rolling hit-rate + error (ticks) for the model against the classic queue-imbalance rule ("more depth on the bid ⇒ next tick up"), scored on identical realized steps. The ML chip is green when the model is out-hitting the rule, red when the naive rule is winning.
- The model resets (and re-warm-starts) on instrument change; its knowledge is instrument-specific.

Under the hood: a bank of recursive-least-squares learners over 19 book features (imbalances, depth skew and deltas, spread, level gaps, sweep costs, signed flow, trade intensity) — honest and lightweight. Math: [reference §4.3](math-reference.md#43-order-book--tradingterminalorderbook) and the [project README](../src/windows/Charts/TradingTerminal.OrderBook/README.md).

## Volume footprint

**Charts → Volume footprint…** — bid/ask cluster chart built from the trade tape. Each column is one time-bucketed bar; each row is a price level; each cell splits into **sell volume (left, red)** and **buy volume (right, green)** with background intensity scaled by size. The total-volume POC row gets a yellow outline; orange / green / red connector lines track the total / buy / sell POC across bars.

Brokers without a native tape get a **synthetic L1-derived fallback** (mid ticks up ⇒ buy print at the ask, down ⇒ sell print at the bid) — flagged `WARN` in the Activity Log and in the status line, so you always know which feed quality you're reading.

> 🖼️ _Screenshot — coming soon_
> 🎬 _Video walkthrough — coming soon_

| Input | Values | Notes |
|---|---|---|
| Instrument | searchable picker | any broker (fallback applies) |
| Interval | 15s / 30s / 1m / 5m | time bucket per bar, default 1m |
| Tick size | free text, default 0.25 | price bucket height |
| Bars | 2–40, default 14 | visible columns (oldest drop off) |

**Stats panel** (floating, top-right): POC slope, buy-POC slope, sell-POC slope (least-squares, price/bar, green/red by direction), cumulative Δ, ticks/sec (2 s window, decays when flow stops), visible volume, buy/sell split %, current POC, plus the **ML vs regression scoreboard** — rolling forecast error + hit-rate for both predictors and the ML sample count (see [ML predictor](#ml-predictor)).

### Regression fits

Seven toggleable fit curves drawn through each POC series (total / buy / sell — color follows the series; dash pattern identifies the kind):

| Fit | Default | Dash | Notes |
|---|---|---|---|
| Linear | on | `5,3` | ordinary least squares — same fit the stats-panel slopes use |
| Quadratic | off | `2,2` | captures curvature in the POC drift |
| Cubic | off | `7,2,2,2` | min 4 valid bars |
| Theil–Sen | off | `12,3` | robust median-of-slopes line; ignores outlier POC bars |
| Exponential | off | `1,3` | log-space OLS; needs all-positive prices |
| Logarithmic | off | `4,2,1,2` | `a + b·ln(x+1)` over column index |
| LOWESS | off | solid | locally weighted linear, tricube kernel, half-sample span |

Fits skip bars whose POC is NaN but always span the full visible range. A fit that can't be computed (too few bars, degenerate geometry) simply doesn't draw. Math: `CurveFitting` in `Core/Quant/`.

### Virtual predictor

Extrapolates every **enabled** fit *N* bars past the last column and draws the per-series **consensus** (mean of the selected fits at each future column) as ghost candles in a shaded forecast region:

- dashed vertical boundary at "now"; columns headed `+1 … +N`
- ghost **body** spans the predicted buy-POC ↔ sell-POC, green/red by predicted POC direction vs the prior column
- dashed **orange tick** at the predicted total POC, with the value printed in the footer
- the fit curves themselves continue through the region, so you can see each kind's own extrapolation against the consensus
- the price axis grows tick-snapped rows when a forecast leaves the traded range

| Input | Values | Notes |
|---|---|---|
| Predicted candles | on/off (default on) | hides the whole forecast region |
| Bars ahead | 1–30, default 5 | horizon (computed and drawn) |

This is curve extrapolation, not a forecast model. Robust kinds (linear / Theil–Sen / LOWESS) extrapolate sanely; cubic and exponential can run away over long horizons by design — uncheck them to take them out of the consensus.

### ML predictor

The learned counterpart to the virtual predictor (📈 Prediction ▾ → **ML prediction (online)**, default on): an **online model that trains itself on the live footprint** — every time a bar completes it grades its previous forecast against what actually happened, learns from the miss, and forecasts again. No training files, no setup; it also uses the regression consensus as one of its inputs, so it learns how much the curve fits are worth.

- **What it predicts, per future column:** total / buy / sell POC **plus total volume and delta** — so its ghost candles are drawn **violet and dotted**, with body *width scaling with predicted volume* and the fill *tinted by predicted delta sign*, and Δ̂ printed in the footer. It caps itself at **8 bars ahead** (a learned model shouldn't pretend to see 30 bars out) while the regression ghosts keep the full horizon; either predictor renders alone if the other is off.
- **Warm-start** (default on): on start it replays up to ~200 bars of recent stored tape through the same bar-building path, so it's usually ready immediately instead of needing ~20 live bars. No local data ⇒ it starts cold and the "ML samples" counter shows it learning.
- **The scoreboard:** the stats panel shows **ML MAE / hit** vs **Reg MAE / hit** — rolling 1-bar-ahead POC error (in ticks) and directional hit-rate, both scored on the *same* realized bars. The ML row is green when the model is beating the regression consensus, red when it's losing — the chart tells you which forecaster has earned trust on this instrument.
- The model resets (and re-warm-starts) whenever instrument, interval or tick size changes — its knowledge is specific to all three.

Under the hood it's a bank of recursive-least-squares learners over 16 engineered footprint features (delta ratio, CVD change, imbalance runs, value-area width, relative volume, feed quality, POC lags, the consensus meta-feature) — honest and lightweight, not a deep net. Math: [reference §4.1](math-reference.md#41-volume-footprint--tradingterminalvolumefootprint) and the [project README](../src/windows/Charts/TradingTerminal.VolumeFootprint/README.md).

## Bookmap + VolBook

One window under **Charts → Bookmap + VolBook**, a custom `WriteableBitmap` + overlay-canvas surface (not ScottPlot) that fuses everything the Bookmap and VolBook® platforms show. One searchable instrument picker + a feature toolbar; selecting an instrument auto-(re)starts the stream; redraws throttled to ~5 fps (200 ms). Price is bucketed into **120 rows**; time scrolls across **320 time-uniform 250 ms columns**, with ~**1600 columns** retained behind the view for playback.

Needs **L2** for the heatmap/DOM; the volume features need the **trade tape**.

> 🖼️ _Screenshot — coming soon_
> 🎬 _Video walkthrough — coming soon_

| Overlay | What it shows |
|---|---|
| Liquidity heatmap | Resting L2 size per (price, time) cell, magma ramp, √-compressed. The Bookmap core. |
| Trade dots | Every print, sized by volume, green buy / red sell. **Large lots** (≥ 5× rolling-mean size) outlined white; **icebergs** (same price+size refilling ≥ 4×) ringed yellow. |
| SVP (Session Range Volume Profile) | Session volume-at-price histogram (buy/sell split) at the left edge, value-area buckets full-strength + outside dimmed, **POC bucket gold**. The VolBook core. |
| VWAP | Developing session VWAP track (`Σpv/Σv`). |
| Value area | Dashed **POC** + **70 % value-area** VAH/VAL from the session profile. |
| CVD panel | Bottom sub-graph: cumulative volume delta (`Σbuy−Σsell`) over per-column net-delta bars. |
| CAV (Cumulative Average Volume) | Compact bottom strip: per-column volume bars + the running session-average-volume line. |
| COB (Current Order Book) | Right-edge column: the live book as a resting-size histogram + numeric sizes at every visible level (spaced) + cumulative bid/ask depth totals. |
| Mid + crosshair | Mid-price line; hover reads out price / nearest resting size / volume-at-price. |

| Control | Effect |
|---|---|
| Timeframe | Column width on the time axis: `250 ms … 1 m` (default 1 s). Larger = slower scroll + lighter render. |
| Per-overlay toggles | Show/hide SVP · COB · CAV · VWAP · value area · CVD panel · trade dots · large lots/icebergs. |
| ⏸ Pause + History slider | Freeze the live scroll and scrub the visible window through the retained history. |
| Mouse wheel / double-click | Zoom the price axis around the cursor (1×…50×) / reset. |

> The old separate cross-asset-volatility and rolling-correlation grids were retired with the standalone heatmaps; that view lives on as the matrices under **Tools → Live correlation matrix**.

## 3D Surface lab

**Charts → 3D Surface lab…** — a dynamic 3D quant surface over one instrument's bars: pick two bucketing dimensions (X/Y), a statistic for the **height** (Z) and another for the **colour** (W), and fly around the result in an interactive HelixToolkit viewport (drag = orbit, wheel = zoom, shift-drag = pan).

> 🖼️ _Screenshot — coming soon_
> 🎬 _Video walkthrough — coming soon_

Two modes:

| Mode | X / Y options | Each cell holds |
|---|---|---|
| **Temporal Aggregation** (seasonality) | Hour of Day · Day of Week · Day of Month · Month of Year | the statistic of all returns that fell in that calendar bucket — "does it rally Tuesday mornings?" |
| **Statistical / Cross-Sectional** | Prior-Return Bucket · Volatility Bucket (20-bar σ deciles) · Volume Decile · Time Lag (bars) | the statistic of **next-period (t+1)** returns conditioned on those two variables — "after a high-vol down bar, what happens next?" |

| Input | Values | Notes |
|---|---|---|
| Instrument / Timeframe / Bars | picker · 1m–1d · default 2000 (min 200) | single instrument; bars from the canonical repository |
| Z / Color statistic | 16 registry metrics | average/median return, std dev, count, P(up), annualized vol, VaR/CVaR 95/99, skew, kurtosis, z-score, normal PDF/CDF, lag-1 autocorrelation, Amihud illiquidity |
| Formula bar (Z/Color) | expressions over metric ids | e.g. `avgret / (1e-6 + stdret)`; `+ − * / ^`, `Log/Exp/Sqrt/Abs`, `Max/Min/Avg/Sum` — overrides the picked metric |
| Peak finder | default on | pins the global maximum; slice cursors park on it |
| Robustness heatmap | default off | recolours each cell by plateau-vs-spike score: **green = stable effect, red = isolated spike (overfit)** — check it before trusting any peak |
| Height × | 0.3–3.0 | render-only Z exaggeration |
| GO LIVE | toggle | streams quotes/trades into a rolling bar window and rebuilds the surface once a second (coalesced; tick rate never becomes rebuild rate) |

Empty buckets stay **NaN and render as holes** — no fake zeros. Two **2D slice viewers** (Z along each axis at a movable cursor) drive translucent cutting planes in the 3D view without rebuilding the mesh. Math: [reference §4.5](math-reference.md#45-3d-surface-lab--tradingterminalsurfacelab) and the [project README](../src/windows/Charts/TradingTerminal.SurfaceLab/README.md).

## Code reference

Each project carries its own README with the full setting/color/math detail:

| What | Where | Per-project README |
|---|---|---|
| Charts window | `src/windows/Charts/TradingTerminal.Charts/` (WebView2 assets under `Assets/`) | [README](../src/windows/Charts/TradingTerminal.Charts/README.md) |
| Order book | `src/windows/Charts/TradingTerminal.OrderBook/` | [README](../src/windows/Charts/TradingTerminal.OrderBook/README.md) |
| Volume footprint | `src/windows/Charts/TradingTerminal.VolumeFootprint/` (bar math: `FootprintFeatures` in Core; fits: `Core/Quant/CurveFitting.cs`) | [README](../src/windows/Charts/TradingTerminal.VolumeFootprint/README.md) — incl. the regression-fit and predictor math |
| Bookmap + VolBook | `src/windows/Charts/TradingTerminal.Heatmap/` (`BookmapHeatmapViewModel` data engine + `BookmapHeatmapWindow` rendering) | [README](../src/windows/Charts/TradingTerminal.Heatmap/README.md) |
| 3D Surface lab | `src/windows/Charts/TradingTerminal.SurfaceLab/` (math: `Core/Quant/Surfaces/` — grid builder, metric registry, formula parser, live bar series) | [README](../src/windows/Charts/TradingTerminal.SurfaceLab/README.md) — incl. the full statistic and formula-grammar detail |

DI: each project ships an `Add…Surface()` extension called from the shell's `App` startup.
