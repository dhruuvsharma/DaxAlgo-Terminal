# Volume Footprint

`TradingTerminal.VolumeFootprint` — **Charts → Volume footprint…**

> Bid/ask cluster chart built from the live trade tape: per-price buy/sell volume cells per
> time-bucketed bar, with three **cell display modes** (bid×ask / delta / volume profile),
> diagonal bid-ask **imbalance** + stacked-run highlighting, per-bar **value-area** shading, a
> right-edge **composite session volume profile**, a mouse **crosshair** with a per-cell read-out,
> POC connector lines, **seven toggleable regression fits** through the POC series, and a
> **virtual predictor** that extrapolates the enabled fits into ghost candles. Brokers without a
> native tape get a synthetic L1-derived fallback. **Display only.**

## Data requirements

| L1 | Bars | Depth | Trade tape |
|:--:|:--:|:--:|:--:|
| ✅ | — | — | optional (synthetic fallback) |

Native tape: IB, Binance, Ironbeam, Simulated. Everything else falls back to a **tick-rule
synthesizer**: mid ticks up ⇒ buy print at the ask (size = ask size); mid ticks down ⇒ sell print
at the bid (size = bid size); unchanged mid ⇒ nothing. The active mode is flagged in the status
line and logged `WARN` to the Activity Log, and every bar carries a `FeedQuality` tag
(`RealTape` / `SyntheticL1`).

## Settings

| Setting | Values | What it does |
|---|---|---|
| Instrument | searchable picker | any catalog instrument; routes to its broker |
| Interval | 15s / 30s / 1m / 5m (default 1m) | wall-clock bucket per bar; bars roll on bucket boundaries |
| Tick size | free text, default 0.25 | price bucket height — one chart row per tick |
| Bars | 2–40, default 14 | visible columns; oldest drop off as new bars form |
| Fits | 7 checkboxes (Linear on by default) | regression overlays, see below |
| Predicted candles | checkbox, default on | shows/hides the forecast region |
| Bars ahead | 1–30, default 5 | predictor horizon |
| Cells | Bid×Ask / Delta / Volume (default Bid×Ask) | per-cell render mode, see **Advanced rendering** |
| Imbalances | checkbox, default on | outline diagonal imbalanced cells + stacked-run markers |
| Value area | checkbox, default on | shade each bar's 70% value area (VAH↔VAL) |
| Profile | checkbox, default on | right-edge composite session volume profile |
| Cell volumes | checkbox, default on | show per-cell figures (off = colour-only heatmap) |
| Zoom | 0.5×–3× slider | vertical row-height zoom |

Changing instrument / interval / tick size restarts the stream and clears the chart. Fit,
predictor, display-mode, overlay and zoom toggles recompute and redraw immediately without
restarting.

## Advanced rendering

- **Cell display modes** — `Bid×Ask` is the classic split (sell left/red, buy right/green);
  `Delta` paints one cell per row coloured by net delta sign (intensity ∝ |delta|); `Volume`
  paints a per-row total-volume profile (blue intensity ∝ total). Alpha scales `40 + 180·v/maxRow`
  in every mode so heavy levels pop.
- **Imbalances** — uses Core's pre-computed diagonal flags (3:1 ratio): an **ask imbalance** (buy
  vol dominates the sell vol one tick below) outlines the buy side bright green `#69F0AE`; a **bid
  imbalance** outlines the sell side `#FF8A80`. The footer shows the bar's longest stacked runs
  (`▲n` buying / `▼n` selling) and the stats panel mirrors them as `Stacked ▲/▼`.
- **Value area** — the 70% market-profile band around POC, expanded by annexing the heavier
  adjacent row until 70% of bar volume is enclosed; drawn as a faint blue band behind the column.
- **Composite profile** — a right-edge gutter histogram of total volume per price across all
  visible bars (sell red then buy green, session-POC row outlined yellow).
- **Crosshair** — hovering the grid draws horizontal/vertical guides and a read-out box with the
  price and, for the hovered bar, that cell's buy / sell / Δ / total.

## How a bar is built

Each trade print is projected to `FootprintPrint` and accumulated into the forming bucket; on
every trade the **entire forming bar is rebuilt** by `FootprintFeatures.BuildBar` (Core) — the
same stateless extractor the APEX engine scores from, so chart and engine numbers always agree.
Per bar, per price row $p$ (bucketed to tick size):

- $\text{buy}_p$, $\text{sell}_p$ — aggressor-side volume at that price
- **POC** — the row with max total volume; **buy POC** / **sell POC** — argmax per side
- $\Delta = \sum_p \text{buy}_p - \text{sell}_p$, and the running session **CVD**

## Colors

Cells and overlays (canvas palette is fixed, theme-independent):

| Color | Where | Means |
|---|---|---|
| `#2E7D32` green fill | right half of each cell | buy (aggressor) volume; background alpha scales `40 + 180·vol/maxVol` so heavy levels pop |
| `#C62828` red fill | left half of each cell | sell (aggressor) volume, same alpha scaling |
| `#FFD54F` yellow outline | one cell per bar | the bar's total-volume POC row |
| `#FFA726` orange line + dots | across bars | total-POC connector; also the dashed tick on predicted candles |
| `#00E676` bright green line + dots | across bars | buy-POC connector **and** buy-POC fit curves |
| `#FF5252` bright red line + dots | across bars | sell-POC connector **and** sell-POC fit curves |
| `#29B6F6` light blue | across bars | total-POC fit curves |
| `#4CAF50` / `#E57373` | footer Δ text, stats slopes, ghost candle stroke | rising/positive vs falling/negative |
| `#29B6F6` @ 7% wash | right of the dashed boundary | the predictor's forecast region |
| `#2E7D32` / `#C62828` @ 22% fill | ghost candle bodies | predicted up / down column |
| `#69F0AE` / `#FF8A80` outline | imbalanced cells, stacked-run footer | diagonal ask (buy) / bid (sell) imbalance |
| `#90CAF9` @ 8% fill, 40% edge | band behind a column | the bar's 70% value area |
| `#9E9E9E` | axis, headers, `+N` labels, predicted POC values | dim chrome text |

So: **series is encoded by color** (blue = total, green = buy, red = sell), **fit kind is encoded
by dash pattern** (table below), and the solid connector lines are the raw data the fits run
through.

## Stats panel (top right)

| Value | Definition |
|---|---|
| POC slope / Buy POC slope / Sell POC slope | OLS slope of that POC price vs column index, in **price per bar** — green rising, red falling. Always computed from the linear fit regardless of which fit checkboxes are on. |
| Cumulative Δ | session CVD including the forming bar |
| Ticks / sec | trade arrivals over a rolling 2 s window (decays to 0 when flow stops) |
| Volume | total volume across visible bars |
| Buy / Sell | aggressor split of that volume, in % |
| POC | the last bar's POC price |

## Regression fits — the math

All fits live in `Core/Quant/CurveFitting.cs` and share one contract: fit the valid
$(x_i, y_i)$ samples ($x$ = column index, $y$ = POC price; NaN-POC bars are skipped) and return
**sampled ŷ at every column**, so closed-form fits and local regression render through the same
polyline path. A fit that can't be computed returns null and simply doesn't draw.

| Fit | Dash | Min bars | Model |
|---|---|:--:|---|
| Linear | `5,3` | 2 | OLS: $\hat y = \alpha + \beta x$, $\beta = \frac{n\sum xy - \sum x \sum y}{n\sum x^2 - (\sum x)^2}$ |
| Quadratic | `2,2` | 3 | least-squares degree-2 polynomial |
| Cubic | `7,2,2,2` | 4 | least-squares degree-3 polynomial |
| Theil–Sen | `12,3` | 2 | $\beta = \operatorname{median}\frac{y_j - y_i}{x_j - x_i}$ over all pairs, $\alpha = \operatorname{median}(y_i - \beta x_i)$ |
| Exponential | `1,3` | 2 | $y = a e^{bx}$ via OLS on $\ln y$ (requires $y > 0$) |
| Logarithmic | `4,2,1,2` | 2 | $y = a + b\ln(x + s)$, shift $s$ chosen so every log argument ≥ 1 (for 0-based columns: $\ln(x+1)$) |
| LOWESS | solid | 3 | locally weighted linear regression, see below |

Implementation notes:

- **Polynomials** are solved by normal equations on $t = (x - \bar x)/s$ with
  $s = \max_i \lvert x_i - \bar x\rvert$ — centring/scaling keeps the Vandermonde moments
  well-conditioned (raw bar indices would be ill-conditioned by the cubic). Gaussian elimination
  with partial pivoting; a near-singular system (e.g. coincident $x$) returns null instead of
  garbage.
- **Theil–Sen** is the robust option: a single wild POC bar (news print, thin synthetic bar)
  shifts the median of pairwise slopes by essentially nothing, where OLS would tilt. $O(n^2)$
  pairs — fine at ≤ 40 visible bars.
- **LOWESS** evaluates, at each column $x_0$, a weighted linear fit over the
  $k = \max(3, \lceil n/2 \rceil)$ nearest samples with tricube weights
  $w_i = \left(1 - (d_i/d_{\max})^3\right)^3$, $d_i = \lvert x_i - x_0\rvert$. Degenerate local
  geometry falls back to the weighted mean. It is the only non-parametric fit, hence the only
  solid curve.

## Virtual predictor — the math

With the predictor on and horizon $N$ (**Bars ahead**), every **enabled** fit is evaluated at the
future columns $x = n, \dots, n+N-1$ (extrapolation is free — the fits already evaluate at
arbitrary $x$). The predicted value per POC series at each future column is the **consensus**:

$$\hat y_{\text{series}}(x) = \frac{1}{|K|}\sum_{k \in K} f_k(x)$$

the mean over the enabled fit kinds $K$ that produced a valid, finite value there. Per ghost
column the window draws:

- **body** spanning consensus buy-POC ↔ sell-POC, filled/stroked green or red by whether the
  consensus total POC rose or fell vs the previous column (dashed stroke = virtual);
- **dashed orange tick** at the consensus total POC, with the value printed in the footer;
- header label `+1 … +N`; the per-kind fit curves also continue through the region so you can see
  each extrapolation against the consensus.

The price axis grows tick-snapped rows when a forecast leaves the traded range, so ghost candles
land at their true level instead of clamping to the chart edge.

**Caveat:** this is curve extrapolation, not a forecast model. Linear / Theil–Sen / LOWESS
extrapolate sanely; quadratic, cubic and exponential can run away over long horizons *by design*
— unchecking a fit removes it from both the overlay and the consensus.

## Code map

| What | Where |
|---|---|
| VM (stream, bar building, fits, predictor) | `VolumeFootprintViewModel.cs` |
| Canvas renderer (cells, overlays, ghost candles) | `VolumeFootprintWindow.xaml.cs` |
| Render models (`RenderBar`, `PocFitCurve`, `PredictedBar`) | `VolumeFootprintModels.cs` |
| Bar math | `Core/MarketData/FootprintFeatures` |
| Fit math | `Core/Quant/CurveFitting.cs` (tests: `tests/…/Quant/CurveFittingTests.cs`) |
