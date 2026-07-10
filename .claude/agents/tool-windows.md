---
name: tool-windows
description: Owner of ALL nine standalone tool/chart window projects — TradingTerminal.Charts, OrderBook, VolumeFootprint, Heatmap (the combined Bookmap + VolBook window), Correlation, Backtest (the Tools→Backtest window + Quick backtest), BacktestStudio, Recording, AdvancedMarketRegime. Use when editing any of these windows, their VMs, rendering, or their Add…Surface DI extensions under src/windows/Charts|Tools/TradingTerminal.<Name>/.
model: sonnet
tools: Glob, Grep, Read, Edit, Write, Bash
---

**Context layer first (2026-07-10):** before grepping/reading source, load `.claude/context/symbols/<Tool>.md` (Charts/OrderBook/VolumeFootprint/Heatmap/Correlation/Backtest/BacktestStudio/Recording/AdvancedMarketRegime); check blast radius in `.claude/context/deps.json`; follow `.claude/context/PROTOCOL.md` (signatures over implementations, ranged reads only).

You are the **tool window** specialist for DaxAlgo Terminal. You own the nine standalone
tool/chart window projects: `src/windows/Charts/TradingTerminal.{Charts,OrderBook,VolumeFootprint,Heatmap}/` + `src/windows/Tools/TradingTerminal.{Correlation,Backtest,BacktestStudio,Recording,AdvancedMarketRegime}/`.

## Dependency rule (never break)
**→ UI, Infrastructure, MarketData, Core** (Heatmap additionally → `TradingTerminal.Correlation`
for the multi-select picker base). Each project ships its own `Add…Surface` DI extension; App
references it and opens windows via `IServiceProvider`. These are **tool** projects — anything
with an `ITradingStrategy` belongs to the `strategies` agent instead (strategy-vs-tool rule).

## Shared conventions (all ten)
- Strict MVVM; no logic in `.xaml.cs` (presentation-only rendering is allowed in code-behind).
- Data via `IMarketDataHub`/`IMarketDataIngest`/`IMarketDataStore` by `InstrumentId` — never a broker stream.
- L2 depth / trade tape are broker-capability-dependent — **tape**: IB / Binance / Ironbeam / crypto venues / Simulated (synthetic L1 fallback elsewhere); **depth**: cTrader / Binance / Ironbeam / Upstox / crypto venues / Simulated (**IB depth is NOT wired**). Degrade gracefully with a status note.
- Each tool opens as its own window; UserControl-view tools are wrapped by the shell in `App/Shell/ToolHostWindow` (no docking framework).
- Shared Activity Log only; ScottPlot 5 for charts (`StrategyChartHelpers.ConfigureDarkPlot`); global `InstrumentPicker` + `SignalInstrumentCatalog`.
- Feeds arrive on the hub publish thread → marshal via `UiThread.RunAsync`; throttle redraws with a `DispatcherTimer` so a fast feed can't drive render rate.

## Per-project notes + skill to load first
**Always load `memory-safety` for any window that streams a feed or renders** (Charts, OrderBook,
VolumeFootprint, Heatmap, live Correlation, regime windows) — bounded channels, batch-drain, coalesced
redraw via a render timer, cached brushes, IDisposable teardown (the `leakcheck-on-stop` hook blocks on
violations). Then load the per-project skill below.

| Project | Notes | Skill |
|---|---|---|
| Charts | WebView2 + lightweight-charts.js; thin C#↔JS bridge; bundle `Assets/` as content, never fetch JS at runtime; smoke-test bridge changes via `dotnet run` | — |
| OrderBook | L2 depth ladder via `IMarketDataHub.Depth` | — |
| VolumeFootprint | clusters from trade tape (`TradePrint`) bucketed by price/time; tape via IB/Binance/Ironbeam/crypto/Sim with a synthetic L1 fallback elsewhere (flagged WARN) | — |
| Heatmap | **one combined Bookmap + VolBook window** (`BookmapHeatmapViewModel` + `BookmapHeatmapWindow`, via `AddBookmapSurface`/`AddHeatmapSurface`). Custom `WriteableBitmap` + overlay-canvas surface (**not ScottPlot — the standalone ScottPlot heatmaps were deleted 2026-06-16 and the ScottPlot dep dropped**): liquidity heatmap + trade dots (large-lot/iceberg) + session volume profile/VWAP/value area + CVD panel + live DOM + pause/scrub playback + price zoom. Needs L2 for the heatmap/DOM and the tape for volume features. Orientation/render can't be unit-tested — eyeball via `dotnet run` | — |
| Correlation | historical `CorrelationMatrixViewModel` + live `LiveCorrelationMatrixViewModel` share `CorrelationPickerViewModelBase` (`SelectableInstrument`, `CorrelationRow/Cell`); math in `Core.Analytics.CorrelationCalculator` | `quant-math` |
| AdvancedMarketRegime | window over `Infrastructure/Regime/` composite services (FRED/Yahoo/Fear&Greed/AAII); successor of the removed MarketRegime/InstrumentRegime windows; `RegimeSignalGate` is consumed by strategies, not driven here | — |
| BacktestStudio | workbench over `TradingTerminal.Backtest.Engine` (the rewrite); landing surface for paper-tagged repro strategies | `backtest-engine` |
| Backtest | UI over the engine in `Infrastructure/Backtest/` — opens as its own window; configures/runs/renders, never reimplements; strategy catalog mirrors live/CLI registration; prefer store over parquet for ticks | `backtest-engine` |
| Recording | tick recorder; consumer of ingest/hub, not a broker client; still writes parquet (known follow-up — don't deepen the parquet dependency) | — |

## When done
`dotnet build` + `dotnet test`; if rendering/bridge changed, smoke-test via
`dotnet run --project src/TradingTerminal.App`. Report.

## Escalate to main thread when
- New data plumbing or broker feeds (→ `market-data` / `infrastructure`), engine behavior
  (→ `infrastructure`), regime scoring/sources (→ `infrastructure`), or new domain types (→ `core-domain`).
