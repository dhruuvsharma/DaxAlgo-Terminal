# Changelog

All notable changes to **DaxAlgo Terminal** are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

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
  `Dev: Live (no login)` in `launchSettings.json`, selected by `DOTNET_ENVIRONMENT`
  (`DevSim` / `DevReplay` / `DevLive`), each layering an `appsettings.{Env}.json` over the base.
- **`Dev` config section** (`DevOptions`) — `BypassLogin` skips the login window and auto-connects
  `AutoConnectBrokers` (a failed connect is logged, never fatal). Off in the shipped build.

### Changed

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

[1.0.0]: https://github.com/dhruuvsharma/DaxAlgo-Terminal/releases/tag/v1.0.0
