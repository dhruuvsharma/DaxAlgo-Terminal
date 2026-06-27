# Bookmap + VolBook

`TradingTerminal.Heatmap` — **Charts → Bookmap + VolBook…** (one window, one project)

> A single live microstructure surface that fuses everything Bookmap and VolBook® show: the L2
> **liquidity heatmap**, **trade dots** (with large-lot / iceberg flags), the session **volume
> profile + VWAP + value area**, a **cumulative-volume-delta** panel, the live **DOM**, and
> **playback** (pause + scrub + price zoom). Drawn on a custom `WriteableBitmap` + overlay canvas
> (not ScottPlot) for full control of the aesthetic.

This project used to host six separate ScottPlot heatmaps (depth, imbalance, volume-at-price, volume
bubbles, cross-asset volatility, rolling correlation). They were folded into — and replaced by — the
combined window below; the cross-asset volatility/correlation grids are still available as the
matrices under **Tools → Live correlation matrix**.

## The window

One searchable instrument picker + a feature toolbar. Selecting an instrument auto-(re)starts the
stream; redraws are throttled to one per **200 ms** (≈5 fps) regardless of feed rate, so a fast book
or tape never floods the UI thread. Needs **L2** for the heatmap/DOM; the volume features need the
**trade tape** (IB, Binance, Ironbeam, Simulated — others draw the book only).

### What it draws

- **Liquidity heatmap** — resting L2 size per (price row, time column), magma ramp (dark → bright),
  √-compressed so thin levels show next to walls. Time-uniform **250 ms columns** (a burst of L2
  updates refreshes the same column rather than shredding the time axis).
- **Trade dots** — every print overlaid on the map, radius √-scaled by size, green buy / red sell,
  positioned by trade time. **Large lots** (a print ≥ 5× the rolling-mean size) get a white outline;
  **icebergs** (the same price+size print refilling ≥ 4×) get a yellow ring.
- **SVP — Session Range Volume Profile** (VolBook core) — a session volume-at-price histogram anchored
  at the left edge, buy/sell split (green/red), semi-transparent over the heatmap. Buckets inside the
  70 % value area show at full strength, those outside are dimmed, and the **POC bucket is filled gold**.
  Price bucketed at ≈ price/1000.
- **VWAP** — the developing session VWAP track (amber): `VWAP = Σ pᵢvᵢ / Σ vᵢ`.
- **Value area** — labelled dashed **POC** (orange) and **70 % value-area** VAH/VAL (blue) from the
  session profile.
- **CVD panel** — a bottom sub-graph: the cumulative volume delta track (cyan, `Σ buy − Σ sell`)
  over faint per-column net-delta bars (buy up / sell down).
- **CAV — Cumulative Average Volume** — a compact bottom strip: per-column traded-volume bars with the
  running session-average-volume line (blue) over them, so columns above/below the session average pop.
- **COB — Current Order Book** — the right-edge column draws the *current* book: a horizontal bar per
  price (bid green / ask red) sized by resting volume, with numeric sizes at every visible level
  (spaced so dense ladders don't overlap) and the cumulative bid/ask depth totals in the footer.
- **Mid track + crosshair** — the mid-price line over the map, and a hover crosshair reading out
  price / nearest resting size / session volume-at-price.

### Toolbar

| Control | What it does |
|---|---|
| Timeframe | Column width on the time axis: `250 ms … 1 m` (default 1 s). A larger timeframe scrolls slower and is lighter to render. |
| SVP · COB · CAV · VWAP · Value area · CVD panel · Trade dots · Large lots/icebergs | Toggle each overlay independently. |
| **⏸ Pause** | Freeze the live scroll and enable the scrub slider. |
| **History** slider | Scrub the visible 320-column window through the retained ~1600-column history. |
| Mouse wheel over the map | Zoom the price axis around the cursor (1×…50×). **Double-click** resets. |

Status bar: best bid (green) / ask (red) / mid / spread / last / VWAP / CVD / total volume / status.

## Code map

| What | Where |
|---|---|
| Shared single-instrument plumbing (picker, stream lifecycle, 200 ms render throttle) | `SingleInstrumentHeatmapViewModelBase.cs` |
| Data engine — rolling buffers, volume profile / VWAP / value area, CVD + per-column delta, large-lot/iceberg classification, playback state | `BookmapHeatmapViewModel.cs` |
| All rendering — bitmap heatmap + overlay canvas (profile, VWAP, value area, dots, CVD panel, DOM, crosshair, zoom) | `BookmapHeatmapWindow.xaml` / `.xaml.cs` |

Opened from **Charts → Bookmap + VolBook…** (or the `heatmap` / `bookmap` / `volbook` command).
