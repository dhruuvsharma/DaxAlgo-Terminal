# TradingTerminal.Strategies.IndexRegimeGraph

The **Index Regime Graph** strategy: predicts an index's direction by running the **Advanced Market
Regime** indicator stack on every constituent stock, then weighting each stock's verdict by its index
membership.

## The idea

For an index family (Dow 30, S&P 500 top-30, …) each constituent carries an **index weight**. For
every constituent the strategy runs the multi-timeframe Advanced Market Regime engine across all
eight timeframes (1m → 1D), collapses those eight needle scores into one **stock score ∈ [-1, +1]**
for the chosen **horizon `t`**, then forms the composite:

```
composite = Σ_stocks (stockScore × normalizedIndexWeight)        ∈ [-1, +1]
```

Weights are renormalised over the constituents that actually returned data, so a name that fails to
fetch drops out cleanly instead of dragging the composite to zero. The composite maps to a five-level
band (`StrongDown … StrongUp`); crossing into a strong band publishes a signal when armed.

The **horizon** only changes how a stock's eight timeframes are blended: *Scalp* leans on 1m/3m/5m,
*Position* on 1H/1D (see `RegimeHorizon` / `TimeframeWeighting` in Core).

## How it fits

- **Pure math + models** live in Core (`TradingTerminal.Core/IndexRegime/`):
  `RegimeHorizon`, `IndexRegimeModels` (`ConstituentRegimeScore`, `IndexRegimeSnapshot`),
  `IndexRegimeAggregator`. The weighted constituent universes are `IndexComponentCatalog` /
  `IndexFamily` in `Core/IndexKScore/` (shared with the Index K-Score Surface strategy).
- **Per-stock engine** is reused, not reinvented: `IAdvancedRegimeProvider.AnalyseAsync(...)`
  (`AdvancedRegimeService` in Infrastructure) — the same provider the Advanced Market Regime tool
  uses. It pulls bars cache-first via `IMarketDataRepository`, so refreshes after the first are cheap.
- **`IndexRegimeGraphViewModel`** fans out across constituents with a `SemaphoreSlim(5)` gate
  (respects IB pacing), re-aggregates progressively as each lands, and drives the heatmap table.
  Analysis runs off the UI thread; all collection mutation marshals through `UiThread`. Progressive
  repaints are coalesced (≤ one per 200 ms) so a fan-out of N constituents never floods the UI thread,
  with a guaranteed authoritative paint at the end of each cycle.

## The view

A **static regime heatmap table** — fast to render, easy to scan. Each constituent is one row, sorted
by its contribution to the composite:

```
SYMBOL  WEIGHT  SCORE  DIRECTION   1m  3m  5m  15m  20m  30m  1H  1D   CONTRIB
```

- The **score / direction** columns are the horizon-blended stock verdict (green = bullish,
  red = bearish), and **contrib** is `stockScore × normalizedWeight` — what the name adds to the
  composite.
- The eight **timeframe cells** are a colour-coded heatmap of that stock's per-timeframe regime score
  (fast → slow), so divergence across timeframes is visible at a glance.
- A **composite hero strip** on top shows the index direction (`Σ output × weight`) and band; the
  footer carries bullish/bearish/ready counts and the synthetic / analysing / armed indicators.

The table virtualizes its rows and uses no per-element bitmap effects, so it stays responsive even
while a refresh is streaming in. (It replaced an earlier fully-connected node-graph visualization that
re-rasterized hundreds of blur/shadow effects per pan/zoom frame.)

## Wiring

`AddIndexRegimeGraphStrategy()` registers the `ITradingStrategy` descriptor, the view-model, the
window, and the `StrategyFactoryRegistration`. It is called from `AddStrategyPlugins()` in
`TradingTerminal.App`. `IAdvancedRegimeProvider` is registered app-side (it is shared with the
Advanced Market Regime tool surface). Signal-mode only — no order placement.
