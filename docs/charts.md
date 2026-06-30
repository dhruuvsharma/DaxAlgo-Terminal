# Charts & order-flow windows

> Last updated: 2026-06-30

### In plain terms

The **Charts** menu holds four ways to *look* at a market, from familiar to exotic:

- **Charts** — an ordinary candlestick price chart with moving-average / RSI / MACD overlays. If
  you've used any trading app, this is the one you already know. *(Windows build only.)*
- **Order book** — the live queue of resting buy and sell orders ("Level 2 / depth"): who's waiting to
  buy or sell, at what price, and in what size.
- **Volume footprint** — instead of just price, it shows *how much* traded at *each price* inside each
  bar, split into aggressive buying vs selling — so you can see where the real activity happened.
- **Bookmap + VolBook** — the "x-ray" view: a colour heatmap of where liquidity is sitting in the
  order book over time, with every trade printed on top, plus a volume profile and a running
  buy-minus-sell line. It's the most information-dense window in the app.

You don't need to read all four; pick the one that matches what you're trying to see. The rest of this
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

**Charts → Order book…** — the full live L2 ladder for one instrument: asks stacked above, bids below, per-level size bars normalized to the largest level on either side, plus cumulative size per level.

> 🖼️ _Screenshot — coming soon_
> 🎬 _Video walkthrough — coming soon_

| Input | Values |
|---|---|
| Instrument | searchable picker (depth-capable broker required) |

Read-outs: **best bid / best ask / spread / mid**, bid & ask **level counts**, and the **last update** timestamp. The status line shows levels-per-side and the snapshot time; everything updates per `DepthSnapshot` from the hub.

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

**Stats panel** (floating, top-right): POC slope, buy-POC slope, sell-POC slope (least-squares, price/bar, green/red by direction), cumulative Δ, ticks/sec (2 s window, decays when flow stops), visible volume, buy/sell split %, current POC.

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

## Code reference

Each project carries its own README with the full setting/color/math detail:

| What | Where (Windows tree; Linux mirrors under `src/linux/Charts/`) | Per-project README |
|---|---|---|
| Charts window *(Windows only)* | `src/windows/Charts/TradingTerminal.Charts/` (WebView2 assets under `Assets/`) | [README](../src/windows/Charts/TradingTerminal.Charts/README.md) |
| Order book | `src/windows/Charts/TradingTerminal.OrderBook/` | [README](../src/windows/Charts/TradingTerminal.OrderBook/README.md) |
| Volume footprint | `src/windows/Charts/TradingTerminal.VolumeFootprint/` (bar math: `FootprintFeatures` in Core; fits: `Core/Quant/CurveFitting.cs`) | [README](../src/windows/Charts/TradingTerminal.VolumeFootprint/README.md) — incl. the regression-fit and predictor math |
| Bookmap + VolBook | `src/windows/Charts/TradingTerminal.Heatmap/` (`BookmapHeatmapViewModel` data engine + `BookmapHeatmapWindow` rendering) | [README](../src/windows/Charts/TradingTerminal.Heatmap/README.md) |

DI: each project ships an `Add…Surface()` extension called from the shell's `App` startup.
