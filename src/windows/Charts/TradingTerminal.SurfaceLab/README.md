# 3D Surface Lab

`TradingTerminal.SurfaceLab` — **Charts → 3D Surface lab…**

> A dynamic 3D quant surface chart: pick two bucketing dimensions (X/Y), a statistic for the
> height (Z) and another for the colour (W), and the lab builds an interactive HelixToolkit
> surface over one instrument's bars — **seasonality mode** (returns bucketed by two calendar
> dimensions, e.g. hour × weekday) or **cross-sectional mode** (next-period statistics
> conditioned on prior return / volatility / volume / lag). Sixteen NaN-safe statistics plus a
> **formula bar**, a **robustness heatmap** that separates stable plateaus from overfit spikes,
> peak pinning, 2D slice viewers — and a **LIVE mode** that streams quotes/trades into a rolling
> bar window and rebuilds the surface once a second. **Display only.**

## Data requirements

| L1 | Bars | Depth | Trade tape |
|:--:|:--:|:--:|:--:|
| live mode | ✅ | — | live mode (no-op fallback) |

One-shot surfaces need only **historical bars** (any broker). LIVE mode additionally subscribes
L1 quotes (mids drive the forming bar) and, where available, the trade tape (sizes drive the
volume/Amihud statistics); brokers without a tape stream quotes only.

## Settings

| Setting | Values | What it does |
|---|---|---|
| Surface mode | Temporal Aggregation (default) / Statistical–Cross-Sectional | see **Modes** below |
| Instrument | searchable picker | single instrument (default BTCUSDT → SPY → first) |
| Timeframe | 1m (default) / 5m / 15m / 1h / 1d | bar interval for history + live rolling window |
| Bars | default 2000, min 200 (live clamp 200–5000) | history depth the surface is computed over |
| X / Y axis | mode-dependent variable + Min/Max/Step where range-editable | the two bucketing dimensions (must differ) |
| Z / Color axis | one of 16 statistics, or a **formula** | height and colour of each cell |
| GENERATE SURFACE | button | one-shot build from history (off the UI thread, cancellable) |
| GO LIVE / STOP LIVE | toggle | stream + rebuild once a second (below) |
| Peak finder | checkbox, default on | pins the global maximum with a marker |
| Robustness heatmap | checkbox, default off | recolours by plateau-vs-spike score (below) |
| Height × | slider 0.3–3.0, default 1.0 | render-only Z exaggeration |

## Modes & axes

**Temporal Aggregation (seasonality).** Every bar's return is dropped into a 2D calendar bucket;
Z aggregates each bucket. X/Y options: **Hour of Day** (0–23), **Day of Week**, **Day of Month**,
**Month of Year**. The classic read: "does this instrument systematically rally Tuesday mornings?"

**Statistical / Cross-Sectional.** Each cell holds a statistic of the **next-period (t+1)**
returns, conditioned on where the current bar sits in two variables: **Prior-Return Bucket**
(linear bins, default −5%…+5% step 1%), **Volatility Bucket** (20-bar rolling σ → deciles),
**Volume Decile**, or **Time Lag (bars)** (conditions the other variable at t−k; only one axis
may be a lag). The classic read: "after a high-vol down bar, what does the next bar do?"

Grids are capped at 81 points per axis; empty buckets stay **NaN and render as holes** — the lab
never paints a zero where there was no data.

## The 16 statistics (`SurfaceMetricRegistry`)

**Bucket aggregates:** Average Return `avgret` · Median Return `medret` · Std Dev `stdret` ·
Frequency `count` · P(return > 0) `probup`.

**Statistical & risk:** Realized Vol (ann.) `vol` — σ·√(periods/yr), the annualization factor
inferred from median bar spacing so 24/7 crypto and session equities both come out right ·
VaR 95% `var95` / VaR 99% `var99` (historical, positive loss) · CVaR 95% `cvar95` (expected
shortfall) · Skewness `skew` · Excess Kurtosis `kurtosis` · Z-Score of mean `zscore` ·
Normal PDF `npdf` / Normal CDF `ncdf` (of the z-score; Abramowitz–Stegun erf) ·
Autocorrelation lag-1 `autocorr1` · Amihud Illiquidity `amihud` — mean(|r|/dollar-vol)×10⁶.

All NaN-safe: degenerate samples (empty, zero variance, too few points) return NaN, not garbage.

### Formula bar (Z / Color)

A non-empty formula **overrides** the picked statistic. Variables are the metric ids above
(e.g. `avgret / (1e-6 + stdret)` — an information-ratio surface; `probup * count`). Operators
`+ − * / ^` (`^` right-associative, binds tighter than unary minus), functions `Log Exp Sqrt Abs`
(unary) and `Max Min Avg Sum` (variadic), case-insensitive. Parsed once per build, evaluated per
cell with per-cell metric caching; a bad formula is rejected at parse time with the error shown
in the banner (only the 16 registry ids resolve as variables).

## Robustness, peak, slices

- **Robustness score** per cell ∈ [0, 1]: RMS difference to the up-to-8 neighbours, normalized by
  the global Z range (×3, clamped). **0 = flat plateau → green** (a stable, believable effect);
  **1 = isolated spike → red** (noise / overfit). The single most important overlay before
  trusting any peak.
- **Peak finder** pins the global maximum; the analytics panel prints its location + value, and
  the slice cursors park on it after each build.
- **2D slice viewers**: two ScottPlot charts — Z along Y at a chosen X, and Z along X at a chosen
  Y — with sliders; dragging a slider moves the translucent cutting planes in the 3D view without
  rebuilding the mesh.

## LIVE mode

**GO LIVE** seeds a rolling `LiveBarSeries` from history, then streams: quote mids build the
forming bar, trade prints add volume. Quotes/trades drain through **bounded DropOldest channels**
(16 384 / 65 536, batch-drain 4 096, one UI marshal per batch); a **coalesced 1 s render timer**
snapshots the rolling window and rebuilds the grid off-thread only when dirty and no build is in
flight — tick rate never becomes rebuild rate. Axis and formula edits apply live; changing the
instrument or timeframe stops the stream. Needs ≥ 30 bars before the first surface ("warming up").

## Viewport

HelixToolkit `HelixViewport3D`: **drag = orbit, wheel = zoom, shift-drag = pan**, view-cube in
the corner. The mesh is a heightmap over the unit square (Z min-max normalized, colour variable
carried per-vertex); NaN cells leave holes. Slice-plane drags redraw only the planes + 2D charts,
not the mesh.

## Code map

| What | Where |
|---|---|
| VM (modes, axes, generate + live pipeline) | `SurfaceLabViewModel.cs` (+ `AxisConfigViewModel.cs`) |
| 3D mesh / axes / peak pin / cutting planes / slice charts | `SurfaceLabView.xaml(.cs)` |
| Axis catalog (modes, calendar + conditioning variables) | `Core/Quant/Surfaces/SurfaceAxes.cs` |
| Statistics registry + per-cell math | `Core/Quant/Surfaces/SurfaceMetrics.cs` |
| Grid builder, peak, robustness, slicing | `Core/Quant/Surfaces/SurfaceGridBuilder.cs` |
| Formula parser | `Core/Quant/Surfaces/SurfaceFormulaParser.cs` |
| Rolling live bar window | `Core/Quant/Surfaces/LiveBarSeries.cs` |
| Tests | `tests/TradingTerminal.Tests.Headless/Quant/SurfaceLabMathTests.cs` |
