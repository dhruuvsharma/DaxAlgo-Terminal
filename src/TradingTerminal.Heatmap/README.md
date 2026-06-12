# Heatmaps

`TradingTerminal.Heatmap` — **Charts → Heatmaps → …** (six windows, one project)

> Scrolling ScottPlot heatmap grids over live market data. Two families: **single-instrument**
> microstructure grids (depth, imbalance, volume-at-price, volume bubbles) and
> **multi-instrument** sampled grids (cross-asset volatility, rolling correlation).

## The two families

### Single-instrument (depth · imbalance · volume-at-price · bubbles)

One searchable instrument picker + Start/Stop. The grid buckets price into **120 rows** over the
observed range and scrolls up to **240 time columns**; rendering is throttled to one redraw per
**200 ms** (≈5 fps) regardless of feed rate, so a fast book never floods the UI thread.

### Multi-instrument (cross-asset volatility · rolling correlation)

Category-grouped multi-select instrument checklist (shared with the correlation matrices), plus a
common **sampler**: on Start every ticked instrument's quote stream is subscribed, the latest mid
is stashed per instrument, and a timer samples them all onto one shared time grid — index-aligned
series without timestamp intersection (the Live Correlation Matrix approach).

| Setting | Values | What it does |
|---|---|---|
| Sample step | 1 / 2 (default) / 5 / 10 sec | how often the sampler ticks |
| Window | 60 / 120 (default) / 240 samples | rolling history per instrument |

## Palettes

| Palette | Used by | Reading |
|---|---|---|
| **Turbo** (sequential) | depth, volume-at-price | dark → bright = small → large magnitude |
| **Balance** (diverging, centred on 0) | imbalance, rolling correlation | red = positive (bid-heavy / correlated), blue = negative (ask-heavy / anti-correlated), pale = zero |
| Per-point colors | bubbles | `#26A69A` buy aggressor, `#EF5350` sell aggressor, grey unknown |
| `#EBEBEB` line | depth & imbalance | the mid-price track overlaid on the grid |

Chrome: `#1E1E1E` background, `#3F3F46` grid, `#DCDCDC` text — matches the shell's dark theme.

## The six windows

### Depth (Bookmap-style) — needs L2

Cell value = **raw resting size** at that price row and moment, Turbo palette. Both sides of the
book show as bright bands; persistent horizontal bands are standing walls, a band that vanishes
just before price reaches it was spoofy. Read-outs: best bid / best ask / mid / columns filled /
last update.

### Order-book imbalance — needs L2

Same scrolling snapshot history, but each cell is **signed**:
$\text{cell} = +\text{size}$ for bid levels, $-\text{size}$ for ask levels, Balance palette
centred on zero. Red below the mid track = stacked support, blue above = stacked resistance.
Same read-outs as depth.

### Volume-at-price — needs trade tape

A time-evolving volume profile: prints are bucketed into **2-second columns**; within a column,
volume sums per price row, Turbo palette — hot cells mark where size actually traded (not where
it was merely quoted). The per-column last trade overlays as a price track. Read-outs: last
price, total volume. Tape brokers: IB, Binance, Ironbeam, Simulated — others leave the grid
empty.

### Volume bubbles — needs trade tape

Every print is one bubble at (time, price). Diameter maps trade size by
$d = 4 + 22\sqrt{\text{size}/\text{max size}}$ px (4–26 px), color = aggressor side. Unlike the
gridded profile this keeps prints distinct, so blocks and sweeps pop out. The last **600 trades**
stay on screen. Read-outs: last price, total volume, bubble count.

### Cross-asset volatility — L1 only, any broker

Instruments on Y, time on X, Turbo palette. Cell value is each instrument's **rolling realized
volatility** at that sample:

$$\sigma_t = \operatorname{std}\bigl(r_{t-19},\dots,r_t\bigr),\qquad r_t = \ln\frac{m_t}{m_{t-1}}$$

over the last **20** sampled mid log-returns (per-sample vol, not annualised — it's a relative
"what's moving" grid; hot rows are the instruments currently churning). Scrolls 240 columns.

### Rolling correlation — L1 only, any broker

N×N grid of the ticked instruments, recomputed every sample: pairwise **Pearson correlation of
mid log-returns** over the sample window, Balance palette (red = move together, blue = move
apart, diagonal ≡ 1). Same math as **Tools → Live correlation matrix** — this is the heatmap
rendering of it. Correlation needs ≥ 2 instruments ticked; shorter windows react faster but are
noisier ($\text{SE} \approx 1/\sqrt{n}$ — at 60 samples, |ρ| under ~0.25 is statistically
indistinguishable from zero).

## Code map

| What | Where |
|---|---|
| Shared single-instrument plumbing (picker, stream, 200 ms throttle) | `SingleInstrumentHeatmapViewModelBase.cs` |
| Shared depth-snapshot machinery (rows/columns, mid overlay) | `DepthColumnHeatmapViewModelBase.cs` |
| Shared multi-instrument sampler (step/window, mid stash) | `SampledHeatmapViewModelBase.cs` |
| Per-window VMs | `DepthHeatmapViewModel.cs`, `ImbalanceHeatmapViewModel.cs`, `VolumeProfileHeatmapViewModel.cs`, `VolumeBubbleHeatmapViewModel.cs`, `VolatilityHeatmapViewModel.cs`, `CorrelationHeatmapViewModel.cs` |
| Frame model + ScottPlot rendering (palettes) | `HeatmapFrame.cs`, `HeatmapRenderer.cs` |
