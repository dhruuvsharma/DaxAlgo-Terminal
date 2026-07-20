# AUDIT — token-leak audit of the existing setup (Phase 1)

> Historical baseline from 2026-07-10. Current topology is authoritative only in the generated
> Windows/Linux masters and dependency graphs; removed project paths below are retained as audit
> evidence, not navigation guidance.

Date: 2026-07-10 · Scope: **Windows tree** (`src/windows/` + `tests/`, excluding `tests/linux/`) per Phase-0 answer; edition focus **Intermediate**.
Method: read-only — `rg`/`awk` LOC census; public-type extraction diffed against the full markdown corpus (44 files: `docs/*.md`, `.claude/**/*.md`, `CLAUDE.md`, `README.md`); hook headers + `settings.json`. No whole-file source reads; nothing modified.

## 1 · Prime token sinks (files > 400 LOC)

- **875** `.cs`/`.xaml` files, **103,104 LOC** total. **49 files > 400 LOC carry 32,135 LOC — 31% of the tree in 5.6% of the files.** Full table: Appendix A.
- Top offenders: `ApexScalperStrategy.cs` 1,846 · `CumulativeDeltaViewModel.cs` 1,355 · `OrderBookViewModel.cs` 1,119 · `VolumeFootprintViewModel.cs` 1,093 · `LiveSignalStrategyViewModelBase.cs` 940 · `IndexKScoreSurfaceViewModel.cs` 861 · `SigmaIcFlowStrategyWindow.xaml.cs` 847 · the two shell `MainWindow.xaml` copies 831/830.
- Per-project LOC (top): Core **17,221** · Infrastructure **14,855** · Tests.Headless **11,248** · MarketData **5,825** · App.Intermediate **4,562** · App.Basic **4,545** · Strategies.SigmaIcFlow **4,390** · UI **4,045** · Login **3,847** · Strategies.CumulativeDelta **2,700** · VolumeFootprint **2,518** · UI.Core **2,246**.

## 2 · API-surface documentation gap (3-module sample)

| Module (path) | Public types | Name-mentioned anywhere in the md corpus | Never mentioned |
|---|---|---|---|
| Core (`src/windows/Core/TradingTerminal.Core`) | 403 | 170 (42%) | **233 (58%)** |
| MarketData (`src/windows/Pipeline/TradingTerminal.MarketData`) | 15 | 8 | **7 (47%)** |
| UI (`src/windows/Shell/TradingTerminal.UI`) | 42 | 13 | **29 (69%)** |

- "Mentioned" = identifier appears **at least once, anywhere** — an optimistic proxy. Even mentioned types get prose name-drops, not signatures; only `docs/architecture.md` carries a handful of key seam signatures.
- Consequence: learning any of these ~270 types (or **any** method signature) requires opening source — usually one of the >400-LOC files in §1. That is the measured "re-read source to learn the API" leak.
- Sample never-mentioned types: MarketData — `MarketDataRepository`-adjacent infra like `ArchiveServiceCollectionExtensions`, `ITelegramAuthPrompt`, `LocalParquetLakeExporter`, `QuestDbDockerService`; UI — `BannerHost`, `CustomThemeFile`, `InstrumentTag`, converter suite; Core — `AdvancedRegimeCalculator`/`AdvancedRegimeSnapshot` family, `Accumulator`, `AnalystBar`.

## 3 · Greppable symbol index — does one exist?

**No.** Inventory: 18 skills, 20 agent files, `MULTI-AGENT.md`, 26 `docs/*.md` — none is a symbol/API index (a phrase-grep for "symbol index" / "API surface" false-positived only `docs/ib-tws-setup.md`). Skills carry recipes and gotchas; docs carry prose. This is the gap the context layer fills.

## 4 · Stop hooks — wired & runnable

| Hook | Wiring (`settings.json`) | Purpose (verified from header) |
|---|---|---|
| `session-start.ps1` | SessionStart, 15s | injects branch/commit/dirty-count orientation |
| `build-on-stop.ps1` | Stop #1, 90s | surfaces `dotnet build` errors; skips when no `.cs/.xaml/.csproj/.props` dirty |
| `verify-on-stop.ps1` | Stop #2, 20s | deterministic **hard block** on layer-graph violations / broker-SDK leaks |
| `leakcheck-on-stop.ps1` | Stop #3, 20s | scans changed UI `.cs` for the 4 known RAM-leak patterns |
| `docsync-on-stop.ps1` | Stop #4, 20s | fires once on structural change (`.sln`/`.csproj`) without doc updates |

All invoked `powershell -NoProfile -ExecutionPolicy Bypass -File …` — runnable as wired (not executed during this audit).

## 5 · Top 5 token-bloating change-types

1. **Shell fix ×3** — `App.Basic` + `App.Intermediate` are ~4.5k-LOC near-identical copies (`MainWindow.xaml` 830/831, `MainWindowViewModel.cs` 632/632) plus the Pro copy. No canonical "these are the 3 files" list exists → each fix re-locates the site in every copy. → `RECIPES/shell-fix-triple.md`.
2. **Strategy-window edits** — 9 `Strategies.*` projects ≈ 17k LOC with 460–1,355-LOC VMs, all funneling through the 940-LOC `LiveSignalStrategyViewModelBase`, which gets re-read to answer "what does the base already give me". → `symbols/` for UI.Core + per-strategy module docs.
3. **Canonical-pipeline changes** — MarketData (5.8k) + Infrastructure (14.9k) + Core types; a store/ingest change fans across `SqliteMarketDataStore` (435), `MarketDataArchiver` (574), and 7 `Real*Client` files (482–759 LOC each). → `deps.json` + `market-data-pipeline-change.md`.
4. **Core signature changes** — the biggest project (17.2k LOC, 403 public types, 58% undocumented) and every project depends on it; one signature change means grepping the world. → `symbols/Core-*.md` splits + `deps.json` blastRadius.
5. **Cross-tree fixes** — every backend fix is made twice (`src/linux/` mirror; out of this run's Windows scope but a standing cost); no checklist names the mirror paths. → `cross-tree-fix.md`.

## Bonus findings

- **CLAUDE.md project-map drift (1 case):** the live-strategy base VM (`LiveSignalStrategyViewModelBase.cs`, 940 LOC) lives in `src/windows/UI/TradingTerminal.UI.Core/`, not `Shell/TradingTerminal.UI` — the Windows tree splits `TradingTerminal.UI` (Shell group) / `TradingTerminal.UI.Core` + `TradingTerminal.Settings` (UI group). CLAUDE.md's map predates the split. Not edited (per constraints); `index.md` will disambiguate.
- `Tests.Headless` is 11.2k LOC with no map from feature → test file; `index.md` gets purpose-only rows for tests.
- Windows tree = **32 projects + 2 test projects** across 10 groups (AI, Backtest, Charts, Core, Pipeline, Sdk, Shell, Strategies, Tools, UI); `TradingTerminal.Windows.Basic.slnf` / `.Intermediate.slnf` verified at repo root — recipes will name them as the narrow build filters.

## 6 · Post-generation discoveries (Phase 2 evidence vs CLAUDE.md)

Extracted from real csproj/`symbols` generation — CLAUDE.md drift, not corrected there yet:
1. **Strategies are SDK plugins** — all 9 strategy projects reference ONLY `DaxAlgo.Sdk.Wpf`; shells hold no strategy references and load them via `AddStrategyPlugins()` → `Infrastructure/Plugins/PluginLoader` (ALC + manifest + Authenticode). CLAUDE.md's graph (`Strategies.* → Infrastructure, UI, Core`) is superseded (ADR-0008).
2. **UI split** — `LiveSignalStrategyViewModelBase` / `LiveStrategyHostServices` / `InMemoryLogSink` live in `TradingTerminal.UI.Core` (`src/windows/UI/`), not `TradingTerminal.UI`.
3. `TradingTerminal.Ai` references Core + Infrastructure only (not UI/MarketData as CLAUDE.md's graph states).
4. `IBrokerClient` lives in `Core/MarketData/`, not `Core/Brokers/`.
5. `DaxAlgo.Sdk.Wpf` is a zero-source facade csproj; `samples/DaxAlgo.SamplePlugin` exists outside `src/` and is a Tests.Headless fixture.

## Appendix A — all 49 files > 400 LOC

| LOC | File |
|---|---|
| 1846 | `src/windows/Strategies/TradingTerminal.Strategies.SigmaIcFlow/Engine/ApexScalperStrategy.cs` |
| 1355 | `src/windows/Strategies/TradingTerminal.Strategies.CumulativeDelta/CumulativeDeltaViewModel.cs` |
| 1119 | `src/windows/Charts/TradingTerminal.OrderBook/OrderBookViewModel.cs` |
| 1093 | `src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintViewModel.cs` |
| 940 | `src/windows/UI/TradingTerminal.UI.Core/LiveSignalStrategyViewModelBase.cs` |
| 861 | `src/windows/Strategies/TradingTerminal.Strategies.IndexKScoreSurface/IndexKScoreSurfaceViewModel.cs` |
| 847 | `src/windows/Strategies/TradingTerminal.Strategies.SigmaIcFlow/SigmaIcFlowStrategyWindow.xaml.cs` |
| 831 | `src/windows/Shell/TradingTerminal.App.Intermediate/MainWindow.xaml` |
| 830 | `src/windows/Shell/TradingTerminal.App.Basic/MainWindow.xaml` |
| 814 | `src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintWindow.xaml.cs` |
| 765 | `src/windows/Strategies/TradingTerminal.Strategies.IndexRegimeGraph/IndexRegimeGraphViewModel.cs` |
| 759 | `src/windows/Pipeline/TradingTerminal.Infrastructure/IronBeam/RealIronBeamClient.cs` |
| 734 | `src/windows/Strategies/TradingTerminal.Strategies.CumulativeDelta/CumulativeDeltaWindow.xaml` |
| 725 | `src/windows/Charts/TradingTerminal.Heatmap/BookmapSurface.cs` |
| 713 | `src/windows/Pipeline/TradingTerminal.Infrastructure/LondonStrategicEdge/RealLondonStrategicEdgeClient.cs` |
| 712 | `src/windows/Pipeline/TradingTerminal.Infrastructure/CTrader/RealCTraderClient.cs` |
| 695 | `src/windows/Strategies/TradingTerminal.Strategies.SigmaIcFlow/SigmaIcFlowStrategyWindow.xaml` |
| 661 | `src/windows/Strategies/TradingTerminal.Strategies.OrderFlowPressureMap/OrderFlowPressureMapViewModel.cs` |
| 646 | `src/windows/Shell/TradingTerminal.Login/LoginWindow.xaml` |
| 632 | `src/windows/Shell/TradingTerminal.App.Intermediate/MainWindowViewModel.cs` |
| 632 | `src/windows/Shell/TradingTerminal.App.Basic/MainWindowViewModel.cs` |
| 631 | `src/windows/Strategies/TradingTerminal.Strategies.OrderFlowSurfaceSpike/OrderFlowSurfaceSpikeViewModel.cs` |
| 628 | `src/windows/Strategies/TradingTerminal.Strategies.IndexRegimeGraph/IndexRegimeGraphWindow.xaml` |
| 589 | `src/windows/Pipeline/TradingTerminal.Infrastructure/Upstox/RealUpstoxClient.cs` |
| 582 | `src/windows/Strategies/TradingTerminal.Strategies.OrderFlowCube/OrderFlowCubeViewModel.cs` |
| 580 | `src/windows/Tools/TradingTerminal.BacktestStudio/BacktestStudioViewModel.cs` |
| 574 | `src/windows/Pipeline/TradingTerminal.MarketData/Archive/MarketDataArchiver.cs` |
| 555 | `src/windows/Pipeline/TradingTerminal.Infrastructure/Ib/IbCuratedCatalog.cs` |
| 540 | `src/windows/Shell/TradingTerminal.Login/LoginViewModel.cs` |
| 527 | `src/windows/Core/TradingTerminal.Core/Ml/OrderBookMicroPredictor.cs` |
| 525 | `src/windows/Charts/TradingTerminal.Heatmap/BookmapHeatmapViewModel.cs` |
| 508 | `src/windows/Charts/TradingTerminal.Charts/ChartsViewModel.cs` |
| 495 | `src/windows/Pipeline/TradingTerminal.Infrastructure/Binance/RealBinanceClient.cs` |
| 486 | `src/windows/Core/TradingTerminal.Core/Quant/Surfaces/SurfaceGridBuilder.cs` |
| 485 | `src/windows/Charts/TradingTerminal.OrderBook/OrderBookWindow.xaml` |
| 482 | `src/windows/Pipeline/TradingTerminal.Infrastructure/Ib/RealIbClient.cs` |
| 482 | `src/windows/Pipeline/TradingTerminal.Infrastructure/Alpaca/RealAlpacaClient.cs` |
| 476 | `src/windows/Tools/TradingTerminal.AdvancedMarketRegime/AdvancedMarketRegimeViewModel.cs` |
| 466 | `src/windows/Strategies/TradingTerminal.Strategies.SigmaIcFlow/SigmaIcFlowStrategyViewModel.cs` |
| 456 | `src/windows/Core/TradingTerminal.Core/IndexKScore/IndexKScoreCalculator.cs` |
| 448 | `src/windows/Tools/TradingTerminal.Backtest/QuickBacktestViewModel.cs` |
| 446 | `src/windows/Pipeline/TradingTerminal.Infrastructure/Simulation/SimulatedBrokerClient.cs` |
| 445 | `src/windows/Charts/TradingTerminal.VolumeFootprint/VolumeFootprintWindow.xaml` |
| 440 | `src/windows/Shell/TradingTerminal.UI/Theming/ThemeManager.cs` |
| 435 | `src/windows/Pipeline/TradingTerminal.MarketData/Store/SqliteMarketDataStore.cs` |
| 416 | `tests/TradingTerminal.Tests.Headless/Quant/SurfaceLabMathTests.cs` |
| 415 | `src/windows/Charts/TradingTerminal.OrderBook/OrderBookWindow.xaml.cs` |
| 408 | `src/windows/Core/TradingTerminal.Core/Ml/FootprintNextBarPredictor.cs` |
| 405 | `src/windows/Pipeline/TradingTerminal.Infrastructure/Research/Sandbox/DockerSandboxRunner.cs` |
