# TradingTerminal.Heatmap — combined Bookmap + VolBook window

**Path** `src/windows/Charts/TradingTerminal.Heatmap/` · 1,813 LOC / 7 files · **Editions** B I P · **Blast: med (leaf window)**

**Purpose.** ONE combined window (the 6 old ScottPlot heatmaps were deleted): liquidity heatmap +
trade dots / large-lot / iceberg detection + volume profile / VWAP / value area + CVD panel + DOM +
playback/zoom. Custom `BookmapSurface` renderer (725 LOC — WriteableBitmap-style hot path).

**Depends on** Core, Infrastructure, UI, UI.Core. **Surface** `symbols/Heatmap.md`.

**Invariants.** Depth+tape via hub; the render surface must not allocate per frame (hoist
brushes/buffers); dispose playback buffers on close. Needs L2-capable broker (IB/crypto).

**Tests** Tests.Headless `~Heatmap` / `~Bookmap`. **Common changes.** Detection thresholds
(iceberg/large-lot), palette/scale, playback. Load `memory-safety` before touching the surface.
