# Charts & order-flow windows

> Last updated: 2026-06-13

Reference for every window under the **Charts** menu: what it shows, what it needs, and every input/parameter it exposes. Each opens as its own window, streams through the canonical pipeline (`IMarketDataHub` / `IMarketDataIngest` — never a broker SDK directly), and uses the shared instrument picker (one global catalog; see [user-guide.md](user-guide.md#add-an-instrument-to-the-catalog)).

## Data requirements at a glance

| Window | Needs | Served by |
|---|---|---|
| Charts | historical bars + L1 | all brokers |
| Order book | L2 depth | IB, cTrader, Binance, Ironbeam, Simulated |
| Volume footprint | trade tape (synthetic L1 fallback) | tape: IB, Binance, Ironbeam, Simulated; fallback: everything else |
| Heatmaps — depth, imbalance | L2 depth | IB, cTrader, Binance, Ironbeam, Simulated |
| Heatmaps — volume-at-price, bubbles | trade tape | IB, Binance, Ironbeam, Simulated |
| Heatmaps — cross-asset vol, rolling correlation | L1 (mid) | all brokers |

A window opened against a broker that can't serve its feed reports it in the status line (footprint falls back to a synthetic tape; the tape heatmaps stay empty).

## Charts (TradingView-style)

**Charts → Charts…** — candlestick charting rendered by Lightweight Charts inside a WebView2; all numbers are computed in C# (`Core` indicators), so chart, backtest, and live values agree.

Loads history from `IMarketDataRepository`, then streams the forming candle live from the hub.

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

| Input | Values |
|---|---|
| Instrument | searchable picker (depth-capable broker required) |

Read-outs: **best bid / best ask / spread / mid**, bid & ask **level counts**, and the **last update** timestamp. The status line shows levels-per-side and the snapshot time; everything updates per `DepthSnapshot` from the hub.

## Volume footprint

**Charts → Volume footprint…** — bid/ask cluster chart built from the trade tape. Each column is one time-bucketed bar; each row is a price level; each cell splits into **sell volume (left, red)** and **buy volume (right, green)** with background intensity scaled by size. The total-volume POC row gets a yellow outline; orange / green / red connector lines track the total / buy / sell POC across bars.

Brokers without a native tape get a **synthetic L1-derived fallback** (mid ticks up ⇒ buy print at the ask, down ⇒ sell print at the bid) — flagged `WARN` in the Activity Log and in the status line, so you always know which feed quality you're reading.

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

## Heatmaps

Six windows under **Charts → Heatmaps**, all ScottPlot-rendered scrolling grids. They come in two families:

**Single-instrument** (depth, imbalance, volume-at-price, bubbles): one searchable instrument picker, Start/Stop, renders throttled to ~5 fps (200 ms). Price is bucketed into **120 rows**; time scrolls across up to **240 columns**.

**Multi-instrument** (cross-asset volatility, rolling correlation): category-grouped multi-select checklist (same picker as the correlation matrices), plus a shared live sampler:

| Input | Values | Notes |
|---|---|---|
| Sample step | 1 / 2 / 5 / 10 sec | default 2 s — every ticked instrument's latest mid is sampled onto one time grid |
| Window | 60 / 120 / 240 samples | default 120 — rolling history length |

### Depth (Bookmap-style)

Cell = **raw resting size** at that price level and moment, sequential palette — both sides of the book light up as bright liquidity bands, with the mid overlaid. Hot horizontal bands = persistent walls.

### Order-book imbalance

Same scrolling L2 history, but cells are **signed**: `+size` on the bid side, `-size` on the ask side, diverging palette centred on zero (bid-heavy → red, ask-heavy → blue). Reads as support/resistance stacking rather than raw magnitude. Read-outs for both depth heatmaps: best bid / best ask / mid / columns filled / last update.

### Volume-at-price

A time-evolving volume profile from the trade tape: prints bucketed into **2-second columns**, summed per price bucket — hot cells mark where size actually traded. The per-column last trade overlays as a price track. Read-outs: last price, total volume.

### Volume bubbles

Every print drawn as a bubble at (time, price): diameter `4 + 22·√(size/max)` px (4–26 px), colored by aggressor side (green = buy, red = sell, grey = unknown). Keeps individual prints distinct, so blocks and sweeps stand out. Last **600 trades** stay on screen.

### Cross-asset volatility

Instruments on Y, time on X, color = each instrument's **rolling realized volatility** (std of the last 20 sampled mid log-returns). A live "what's moving" grid — hot rows are the instruments currently churning.

### Rolling correlation

N×N grid of the ticked instruments colored by live pairwise **Pearson correlation of mid log-returns** over the sample window, recomputed every sample (diverging palette: red = move together, blue = move apart). Same math as the Live Correlation Matrix (**Tools → Live correlation matrix**) — this is the heatmap rendering of it.

## Code reference

Each project carries its own README with the full setting/color/math detail:

| What | Where | Per-project README |
|---|---|---|
| Charts window | `src/TradingTerminal.Charts/` (WebView2 assets under `Assets/`) | [README](../src/TradingTerminal.Charts/README.md) |
| Order book | `src/TradingTerminal.OrderBook/` | [README](../src/TradingTerminal.OrderBook/README.md) |
| Volume footprint | `src/TradingTerminal.VolumeFootprint/` (bar math: `FootprintFeatures` in Core; fits: `Core/Quant/CurveFitting.cs`) | [README](../src/TradingTerminal.VolumeFootprint/README.md) — incl. the regression-fit and predictor math |
| Heatmaps (all six) | `src/TradingTerminal.Heatmap/` (`HeatmapRenderer`, per-window VMs, shared bases) | [README](../src/TradingTerminal.Heatmap/README.md) |

DI: each project ships an `Add…Surface()` extension called from `App.xaml.cs`.
