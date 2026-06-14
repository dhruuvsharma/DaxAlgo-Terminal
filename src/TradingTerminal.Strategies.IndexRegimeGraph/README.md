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
  (respects IB pacing), re-aggregates progressively as each lands, and drives two synchronized views.
  Analysis runs off the UI thread; all collection mutation marshals through `UiThread`.

## The view

A **feed-forward neural net**, left → right, with every layer visible at once and fully connected:

```
companies  →  indicators  →  timeframes  →  output  →  ×weight  →  signal
 (N inputs)     (18)           (8)            (1)                    (1)
```

- **Company** nodes (input layer) are sized by index weight and coloured by their stock score.
- The **indicator** and **timeframe** hidden layers show the index aggregate by default. **Click a
  company** to *focus* it: its pathway lights up and the hidden layers + output switch to that
  company's values (click it again, press `Esc`, or "Clear focus" to return to the aggregate).
- The **signal** node always shows the composite (Σ output × weight) and its band.

Interaction (pure view state in code-behind, driving one `MatrixTransform`):

| Gesture | Action |
|---|---|
| Drag background | Pan |
| Mouse wheel / `+` `−` buttons / `+`/`-` keys | Zoom about cursor/centre |
| `⤢ Fit` / `Home` | Fit the whole net to the viewport |
| Drag a node | Move it (synapses follow) |
| Click a company | Focus / unfocus its pathway |
| Arrow keys | Pan · `Tab` cycle companies · `Esc` clear focus |

A graph-paper backdrop makes pan/zoom obvious, and the view fits to the window automatically when
the graph (re)builds. A right-hand panel lists every constituent (symbol, weight, score,
contribution, band) sorted by contribution, with the composite headline on top.

## Wiring

`AddIndexRegimeGraphStrategy()` registers the `ITradingStrategy` descriptor, the view-model, the
window, and the `StrategyFactoryRegistration`. It is called from `AddStrategyPlugins()` in
`TradingTerminal.App`. `IAdvancedRegimeProvider` is registered app-side (it is shared with the
Advanced Market Regime tool surface). Signal-mode only — no order placement.
