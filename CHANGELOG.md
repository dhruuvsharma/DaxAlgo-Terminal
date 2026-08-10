# Changelog

All notable changes to **DaxAlgo Terminal** are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- **Open-core split (2026-07-09).** The Professional edition is now closed source: the
  `TradingTerminal.App` shell, the AI tool windows (MarketAnalyst / FactorResearch / MlFeatures /
  BacktestAnalysis / PaperLab), the Machine Learning windows (Stationarity / ArimaGarch /
  KalmanFilter), SurfaceLab, BubbleChart, LseBacktest, QuantConnect, the `daxalgo-backtest` CLI
  and the installer moved to a private overlay repo that consumes this repo as a git submodule.
  The Basic edition remains fully open source here and is now the only application shell in this
  repository.
- **License changed MIT → AGPL-3.0** for all code going forward (the `src/windows/Sdk/` plugin SDK
  stays MIT). The proposed plugin linking exception is still a draft and is not in force, so the SDK's
  MIT package license must not be represented as an active exception from the linked AGPL host.
  Previously published code remains MIT in the repo history.

- **Strategy renamed — "APEX microstructure scalper v2" → "Σ⁻¹·IC Order-Flow Optimizer".** Live
  project `TradingTerminal.Strategies.ApexScalper` → `TradingTerminal.Strategies.SigmaIcFlow`
  (classes, namespace, DI `AddSigmaIcFlowStrategy()`); live strategy id `apex.scalper` →
  `sigma.ic.flow`; backtest/CLI id `apexScalper` → `sigmaIcFlow` (legacy `apex`/`apexscalper`
  aliases resolved at the time). The then-current engine-side class retained the internal name
  `ApexScalperStrategy` (`Infrastructure/Backtest/Strategies/`).
- **Main window launches maximized** (`WindowState="Maximized"`).
- **Shell redesign — AvalonDock removed.** The current `MainWindow` is a full-width
  strategy/visualizer catalog with a collapsible Activity Log. Retained surfaces open in standalone
  host windows: Backtest Studio, recorder, notifications, archive settings/history, and Theme Studio.
  The recorder is opened by the header's REC chip rather than a menu item. The public menu bar is
  limited to File, View, Backtest Engine, Strategy Studio, Data, the deliberately disabled Execution
  Engine, Settings, and Help.
- **Basic broker composition expanded.** The sole public shell now registers both keyless and
  credentialed broker sets and starts at broker selection. There is no product-account or entitlement
  gate. Interactive Brokers and NinjaTrader remain conditional on their local vendor DLLs.
- **Strategies moved to runtime plugins.** The live strategy catalog now ships empty; first-party
  strategies are external plugins built on the published SDK. Backtest Studio retains its own built-in
  demonstration kernels. Catalog cards now model strategies and visualizers, while installable
  visualizers remain pending a package/contribution format.
- **SDK 0.3 sandbox authoring published.** `DaxAlgo.Sdk`, `DaxAlgo.Templates`, the strategy-bundle
  packages, and both `daxalgo-sandbox-*` templates are available at version `0.3.0`. The templates
  scaffold capability-scoped `IStrategyKernel` / `IVisualizer` units with direct tests and the
  `DAX3001` analyzer.
- **Public documentation refreshed.** The README now leads with the AI-native Vibe Code workflow,
  distinguishes SDK 0.3 sandbox units from legacy `.daxplugin` and `.daxstrategy` artifacts, restores
  accurate author/dependency/trademark credits, and removes unshipped media placeholders. Focused
  implementation guides remain under `docs/`.

### Removed

- `Shell/DockTab.cs`, `Shell/TickerTapeViewModel.cs`, `UI/DockTabStyleSelector.cs`, and the
  `Dirkster.AvalonDock` + `Dirkster.AvalonDock.Themes.VS2013` package references.
- The public Intermediate and Professional shell projects, the product-account gate, and the public
  Tools, Charts, and Research menus and their former windows. Removed surfaces include the correlation
  matrix, volume footprint, order book, Bookmap/heatmap, 3D surface lab, bubble chart, Paper Lab,
  machine-learning windows, and market-regime windows.

## [1.1.0] — 2026-06-15

### Added

- **Filtered Order-Flow Imbalance strategy** (`TradingTerminal.Strategies.FilteredOrderFlow`,
  id `filtered.orderflow.imbalance`) — a research-paper strategy implementing trade-based order-book
  imbalance `OBI(T)` (net signed-trade count over a rolling window) from Anantha, Jain & Maiti (2025),
  *"Order-Flow Filtration and Directional Association with Short-Horizon Returns"*
  ([arXiv:2507.22712](https://arxiv.org/abs/2507.22712)). Classifies `OBI(T)` on a 9-bin regime grid,
  signals on strong same-sign regimes, and tracks **filtered vs. unfiltered** `OBI(T)` side-by-side
  (the paper's core comparison). Tape-primary; shared OBI math in `Core/MarketData/OrderFlowImbalance.cs`;
  also registered in the Backtest tab catalog and the `daxalgo-backtest` CLI (`filteredOrderFlow`).
- **"Research paper" strategy tag** (`ITradingStrategy.ResearchPaperUrl`) — strategies derived from
  published papers surface a clickable "📄 Research paper" pill in the Strategies pane that opens the
  source paper. Currently set on Filtered Order-Flow Imbalance.
- **Application theme system** — selectable app-wide themes (Dark + new **Monochrome**) via a Theme menu,
  backed by `UI/Theming/` and `Brushes.xaml`/`Dark.xaml` token updates.
- **Binance backend** (`BrokerKind.Binance`, `RealBinanceClient`) — real, live crypto market data
  (bars, L1 `@bookTicker`, L2 `@depth`, `@trade` tape) over Binance's **public** WebSocket + REST,
  with **no API key and no account**. Always registered (no SDK/NuGet — raw `ClientWebSocket` +
  `HttpClient` + `System.Text.Json`), with a per-stream reconnect loop and a no-fields
  **Binance (no login)** tile. The zero-credential way to run the terminal against a real feed.
  Configured via the `Binance` section (`BinanceOptions`); geo-blocked regions can repoint to
  Binance.US / data-api.binance.vision hosts.
- **`Simulated` broker** (`BrokerKind.Simulated`, `SimulatedBrokerClient`) — an always-registered,
  in-process `IBrokerClient` with no broker and no network. Two feed modes: a deterministic
  random-walk (**Synthetic**) or speed-scaled **Replay** of the local market-data store. Supports
  trade tape and L2 depth. Configured via the `SimulatedBroker` section (`SimulatedBrokerOptions`).
- **Dev launch profiles** — `Dev: Simulated (offline)` / `Dev: Replay (local DB)` /
  `Dev: Live (no login)` were added in `launchSettings.json`, selected by `DOTNET_ENVIRONMENT`
  and layering environment-specific settings over the base. The replay profile was later retired.
- **`Dev` config section** (`DevOptions`) — `BypassLogin` skips the login window and auto-connects
  `AutoConnectBrokers` (a failed connect is logged, never fatal). Off in the shipped build.

### Changed

- **Login window redesign** — reworked broker sign-in UI (`LoginWindow`, `LoginViewModel`) with a
  shared `BrokerLoginFormBase` and an `InjectedFormHost` for per-broker credential forms.
- **Order Book window overhaul** — expanded `OrderBookViewModel` / rendering with new `OrderBookModels`.
- **Correlation & Heatmap windows** — refreshed XAML / layout to match the new theme tokens.
- **Removed per-broker synthetic fallbacks** (`Fake*Client`). Real broker clients are now registered
  only when their SDK is available (IB/NT gated on a resolved DLL via `HAS_IBAPI`/`HAS_NTAPI`;
  cTrader/Alpaca always restore from NuGet); offline runs use the new `Simulated` broker instead.
  The `InteractiveBrokers`/`NinjaTrader` `UseRealClient` keys are no longer read.

## [1.0.0] — 2026-06-05

First public release. A modular, multi-broker WPF trading terminal — **data and signals only,
no live order execution.**

### Added

- **Four broker backends** behind one `IBrokerClient` seam — Interactive Brokers (TWS API),
  NinjaTrader 8 (NTDirect P/Invoke), cTrader (Spotware Open API 2.0), Alpaca (REST + WebSocket).
  Synthetic `Fake*Client` random-walks run with no broker installed.
- **9 live strategies** behind one `IBacktestStrategy` plug-in seam — Ornstein-Uhlenbeck,
  Volatility-Targeted, Order-Flow Toxicity (VPIN), Cumulative Delta, APEX Scalper, and the 3D
  regime-cube family (Order-Flow Cube, Order-Flow Surface Spike, Imbalance Heat Front,
  Index K-Score Surface).
- **Canonical market-data pipeline** — broker-neutral `InstrumentId`, Rx fanout hub, ref-counted
  ingest, and a pluggable store: SQLite (zero-config default), PostgreSQL + TimescaleDB, or QuestDB.
- **QuestDB Docker bootstrap** — auto-starts (and can launch Docker Desktop for) the QuestDB
  container on demand, then arms tick persistence live without an app restart.
- **Tick-level backtest engine** — fee models (zero / maker-taker / bps), risk caps, L1 fill model,
  Parquet tick reader/writer, full stats suite. Headless CLI (`daxalgo-backtest`) with
  `run` / `synth` / `sweep` / `walkforward` / `mc` / `tca` / `features`.
- **Tool windows** — TradingView-style charts (WebView2), L2 order book, volume footprint,
  Bookmap-style depth/liquidity heatmaps (depth, imbalance, volume-at-price, volume bubbles,
  cross-asset volatility, rolling correlation), correlation matrix (static + live).
- **Market-regime composite** — 0–100 risk-on/risk-off score from Yahoo / FRED / CNN Fear & Greed /
  AAII, with per-instrument and Markov regime analyzers and an opt-in signal gate.
- **AI Market Analyst** — four-agent LangGraph Python sidecar reached over loopback HTTP/JSON,
  hot-swappable Null↔Http; degrades gracefully when the sidecar isn't running.
- **Notifications** — bounded channel + hosted dispatcher fanning signals to Telegram and Discord,
  with an optional LLM commentary enricher.
- **Telegram market-data archive** — parquet bundling, 2 GB split parts, sha256-verified, manifest
  store and retention pruning so the local store can prune safely.
- **Universal Activity Log**, MahApps Metro + AvalonDock VS2013 Dark shell, multi-broker login.
- **Support the developer** window — a once-per-launch thank-you with a "write to the developer"
  feedback channel (delivered via the user's own mail client). All features are and remain free.
- **Windows installer** (Inno Setup) — per-user install of the self-contained app, with an opt-in
  page that downloads and installs the external dependencies on demand (WebView2 Runtime for Charts,
  Docker Desktop for the QuestDB store). Shipped alongside a portable zip on every tagged release.

[1.1.0]: https://github.com/dhruuvsharma/DaxAlgo-Terminal/releases/tag/v1.1.0
[1.0.0]: https://github.com/dhruuvsharma/DaxAlgo-Terminal/releases/tag/v1.0.0
