# Volume Footprint

`TradingTerminal.VolumeFootprint` — **Charts → Volume footprint…**

> Bid/ask cluster chart built from the live trade tape: per-price buy/sell volume cells per
> time-bucketed bar, with three **cell display modes** (bid×ask / delta / volume profile),
> diagonal bid-ask **imbalance** + stacked-run highlighting, per-bar **value-area** shading, a
> right-edge **composite session volume profile**, a mouse **crosshair** with a per-cell read-out,
> POC connector lines, **seven toggleable regression fits** through the POC series, a
> **virtual predictor** that extrapolates the enabled fits into ghost candles, and an
> **a learned forecast** that prefers an optional external batch provider when one is available and
> otherwise falls back safely to the built-in online RLS learner. It draws violet forecast candles
> (with predicted volume and delta) next to the regression ones; distributional batches also show
> uncertainty bands. Brokers without a native tape get a synthetic L1-derived fallback.
> **Display only.**

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
| Predicted candles | checkbox, default on | shows/hides the regression forecast |
| Bars ahead | 1–30, default 5 | forecast horizon (the learned provider/fallback path caps at 8, see below) |
| ML prediction (online) | checkbox, default on | shows/hides the preferred batch forecast or online RLS fallback (violet dotted ghosts) |
| Warm-start from history | checkbox, default on | rebuilds provider context and warms the online fallback from stored tape on (re)start |
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
| `#B388FF` violet, **dotted** `1,3` | narrower ghost bodies + POC ticks + `Δ̂` footer | the learned forecast (dash = regression, dots = provider/fallback) |
| violet-tinted green / red @ 19% fill | ML ghost bodies | predicted delta ≥ 0 / < 0 |
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
| ML MAE / hit | rolling 1-step POC error (ticks) + directional hit-rate of the online RLS fallback — green when it's beating the regression consensus on MAE, red when losing |
| Reg MAE / hit | same two numbers for the **regression consensus**, scored on the same realized bars |
| ML samples | sealed bars the online RLS fallback has learned from (warm-start bars included) |

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

## Learned forecast — provider first, online fallback

### In plain terms

The regression predictor above only ever looks at *where the POC has been* and extends the curve.
The learned path can use more of each completed footprint bar — delta, CVD, imbalance runs,
value-area width, relative volume and feed quality. The chart first asks an optional external batch
forecast provider. When that provider returns an available batch, its forecast is preferred. If no
provider is configured, its coordinate/history requirements are not met, or inference fails, the
built-in online RLS learner remains the safe fallback. Neither path creates an order signal.

### Optional batch provider

The provider receives complete, contiguous sealed bars through the neutral forecast contract and
returns exact future intervals in one batch. The shared chart requests at most
`FootprintPredictorOptions.MaxHorizon` — currently **8 bars** — even when **Bars ahead** is higher;
regression ghosts may still extend to 30. Stored-tape warm-start also builds provider history, so an
available provider can render before the next live bar seals.

Each horizon can carry marginal q10/q50/q90 forecasts. The chart uses medians for the POC,
buy/sell POCs, volume and delta point display. A distributional batch additionally draws a dotted
median low→high wick and a translucent q10→q90 POC band. Provider identity and implementation stay
outside the chart; unavailable or failed results simply leave the online fallback in place.

### Online RLS fallback

`FootprintNextBarPredictor` (`Core/Ml/`) learns on every completed bar. Each seal first grades the
forecast made earlier, then teaches the model, then asks for the next forecast. The stats panel
shows this fallback's running score against regression; those rows do not score an external batch.

A bank of **recursive-least-squares** learners (`OnlineLinearRegression`, exponential forgetting
$\lambda = 0.995$): one independent learner per (**target × horizon**) — 5 targets × 8 horizons —
so an outlier in one target's residual can't destabilise another's coefficients. Horizons are
**direct**: the horizon-$h$ learner fits $\text{target}(t+h) - \text{reference}(t)$ straight from
the features at $t$, rather than iterating a 1-step model, which would need invented future
feature vectors and compounds its own error. The online bank and optional batch request share the
$\min(\text{Bars ahead}, 8)$ learned-forecast cap while regression ghosts can run to 30.

**Targets** (per horizon $h$, reference bar $t$, tick size $\tau$) — all stationary by
construction:

$$y_1 = \frac{\text{poc}_{t+h} - \text{poc}_t}{\tau},\quad
  y_2 = \frac{\text{buyPoc}_{t+h} - \text{poc}_t}{\tau},\quad
  y_3 = \frac{\text{sellPoc}_{t+h} - \text{poc}_t}{\tau}$$

$$y_4 = \ln(1 + v_{t+h}) - \overline{\ln v}_t,\qquad
  y_5 = \operatorname{clamp}\!\left(\frac{\Delta_{t+h}}{\max(1, \bar v_t)},\, \pm 3\right)$$

where $\overline{\ln v}_t$ / $\bar v_t$ are EWMAs (half-life 16 bars) of log-volume / volume,
snapshotted at prediction time so learning inverts exactly what was predicted. Forecast prices,
volume and delta are reconstructed by inverting these transforms.

**Features** (16 dims, all price terms in ticks): bias; three lagged POC changes; buy−sell POC
spread; POC position inside the bar range; value-area width; bar range; delta ratio
$\Delta_t/v_t$ + its EWMA; relative log-volume; 3-bar CVD change (volume-normalised); stacked-run
imbalance $\text{▲} - \text{▼}$; the `FeedQuality` multiplier (so synthetic-tape bars carry less
weight); and the **regression consensus as a meta-feature** — $(\hat y_{\text{cons}} -
\text{poc}_t)/\tau$ plus a validity flag, so the model learns how much the curve fits are worth
rather than being told.

Features are standardised by an **exponentially-weighted online scaler** (`OnlineFeatureScaler`:
Welford mean/variance with decay, output clamped to ±5) before both prediction and learning — raw
prices into RLS would blow up the inverse-covariance matrix on the first outlier bar.

### Fallback training loop, warm-start, scoring

- **Walk-forward, ex-ante by construction:** at each seal the fallback scores/learns every pending
  snapshot whose target bar just realised, then predicts. The regression baseline captured at
  bar $t$ is `Predicted[0]` as published *before* bar $t+1$ existed — fallback and regression are graded
  on identical realized bars (`RollingForecastMetrics`: MAE in ticks + directional hit-rate over a
  100-score window; a zero realized move counts as a hit only if the forecast was < 0.5 tick).
- **Warm-start:** on (re)start the window replays up to **interval × 200 bars (≤ 24 h)** of stored
  tape (`IMarketDataStore.ReadTradesAsync`) through the *same* `FootprintTimeBucketer` the live
  stream uses. Those sealed bars become provider context and, when needed, train the online
  fallback off the UI thread; a watermark prevents the seam bar being learned twice. No stored
  tape ⇒ the fallback starts cold and the provider waits for live context.
- **Readiness:** fallback forecasts render only after ≥ 20 updates per learner ("ML samples" in
  the stats panel); the fallback resets whenever instrument / interval / tick size changes (its
  coefficients are scoped to all three). Provider readiness is reported through its own result.

### Rendering

Learned ghosts share the forecast columns with the regression ghosts but are **violet and dotted**
(`1,3` — regression bodies are dashed `3,2`): body spans the predicted buy↔sell POC, body
**width scales with predicted volume** ($0.22 + 0.5\,\hat v/\bar v_{\text{visible}}$ of the
column, clamped to $[0.22, 0.86]$), fill **tint follows the predicted delta sign**, a dotted
violet tick marks the predicted total POC, and the footer prints $\hat\Delta$. With the
regression predictor unchecked the learned forecast supplies its own wash/boundary/headers, so
either predictor works alone. Distributional provider results add the low→high wick and POC
q10→q90 band described above; the online fallback keeps the median-only ghost.

**Fallback caveat:** the RLS learner is linear in its 16 engineered features — it adapts to regime changes
(forgetting) and quantifies itself honestly (the MAE/hit rows), but it is a lightweight online
model. Treat a green "ML MAE / hit" row as *"the fallback features currently carry signal here"*,
not as a trading system or as validation of an external batch.

## Code map

| What | Where |
|---|---|
| VM (stream, seal hook, fits, provider preference, RLS warm-start) | `VolumeFootprintViewModel.cs` |
| Canvas renderer (cells, overlays, ghost candles) | `VolumeFootprintWindow.xaml.cs` |
| Render models (`RenderBar`, `PocFitCurve`, `PredictedBar`, `MlPredictedBar`) | `VolumeFootprintModels.cs` |
| Bar math | `Core/MarketData/FootprintFeatures` |
| Time bucketing (shared live + warm-start) | `Core/MarketData/FootprintTimeBucketer.cs` (tests: `tests/…/MarketData/FootprintTimeBucketerTests.cs`) |
| Fit math | `Core/Quant/CurveFitting.cs` (tests: `tests/…/Quant/CurveFittingTests.cs`) |
| Batch-provider contract and null fallback | `Core/Ml/FootprintForecastProvider.cs` |
| Online fallback (RLS bank, features, targets) | `Core/Ml/FootprintNextBarPredictor.cs` (tests: `tests/…/Ml/FootprintNextBarPredictorTests.cs`) |
| Online standardiser / rolling scores | `Core/Ml/OnlineFeatureScaler.cs` · `Core/Ml/RollingForecastMetrics.cs` |
