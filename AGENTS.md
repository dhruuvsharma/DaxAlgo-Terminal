# DaxAlgo Terminal — Codex Working Guide

A modular **multi-broker** WPF trading terminal. WPF + .NET 9. Twelve brokers behind one `IBrokerClient` seam: Interactive Brokers (TWS API), NinjaTrader 8 (NTDirect P/Invoke), cTrader (Spotware Open API 2.0), Alpaca (REST + WebSocket), Ironbeam (futures FCM, REST + WebSocket API v2), London Strategic Edge (free multi-asset L1 + history, WebSocket + REST), Upstox (Indian markets, OAuth2), and keyless public crypto feeds Binance / Coinbase / Bybit / Kraken / OKX (the latter four unverified live) — plus the offline Simulated backend. **Data/signals only — no live order execution.**

This is the always-loaded core. Detail lives in **skills** (lazy-loaded by trigger) and **docs/**. Don't re-derive conventions each session — load the matching skill, or `navigator` for "where does X live".

## Work tracking — GitHub issues are the source of truth

Ongoing initiatives and multi-session work are tracked as **GitHub issues** (`gh issue`), not in model memory or the user's head. Two living trackers:
- **[#3](https://github.com/dhruuvsharma/DaxAlgo-Terminal/issues/3)** — Windows scale refactor epic (6-phase Surface-Lab-grade UX + perf pass; per-phase checklist + commit hashes).
- **[#4](https://github.com/dhruuvsharma/DaxAlgo-Terminal/issues/4)** — Roadmap / backlog of planned & in-flight initiatives.

**The pipeline (do this without being asked):** when you finish a slice of tracked work, **update its issue** — tick the checkbox and drop the commit hash. When a new initiative starts, **open an issue** (`gh issue create`, apply labels: `epic`/`refactor`/`ui-ux`/`performance`/`charts`/`strategies`/`tools`/`ai-ml`/`distribution`/`roadmap`) and link it from #4. When active work spins out of the backlog, promote it to its own labelled issue. Prefer a short issue comment or checkbox tick over expanding a memory file. Write issue bodies via `--body-file` (PowerShell 5.1 mangles inline `-m`/`--body` with embedded double quotes).

## ⚠️ Two independent trees — NO shared code (2026-06-27)

The repo is **forked into two fully independent codebases with zero shared projects** (done so Linux/Avalonia work can never destabilize the Windows/WPF build):

| Tree | Root | Solution | TFM | UI |
|---|---|---|---|---|
| **Windows** | `src/windows/` | `TradingTerminal.Windows.slnx` | `net9.0-windows7.0` | WPF (MahApps) |
| **Linux** | `src/linux/` | `TradingTerminal.Linux.slnx` | `net9.0` | Avalonia |

- Each tree owns its **own copy** of the backend (Core, MarketData, Infrastructure, Backtest.Engine/Cli, UI.Core, Settings) and of every strategy/tool/AI/chart project. `src/shared/` is gone; **no multi-targeting** remains.
- Type **namespaces are intentionally identical** across trees (`TradingTerminal.Core`, etc.) — the two never compile together, so there's no clash. A change in one tree does **not** propagate; **a fix that should apply to both must be made twice** (once per tree).
- Windows-only projects (e.g. `TradingTerminal.Charts` WebView2 charts, the `Ml.*` windows) exist only under `src/windows/`. The Avalonia shell is `src/linux/Shell/TradingTerminal.App.Avalonia`.
- Tests: `tests/TradingTerminal.Tests` (WPF) + `tests/TradingTerminal.Tests.Headless` → Windows tree; `tests/linux/TradingTerminal.Tests.Headless` → Linux tree.
- Project-map/skill descriptions below still name projects correctly; just prepend `src/windows/<group>/` (or `src/linux/<group>/`) to the old `src/` paths.

## Stack

- **TFM**: `net9.0-windows7.0` (in `Directory.Build.props`). Don't rename to `net8.0-windows` or strip the `7.0`.
- **MVVM**: `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`). **Shell**: MahApps Metro — **no docking framework** (AvalonDock removed 2026-06-18). Every tool/strategy/chart opens as its own `Window`; the `MainWindow` is a full-width strategy catalog with a collapsible bottom activity-log drawer, and tools exposing a `UserControl` view are wrapped in `App/Shell/ToolHostWindow`. **Charts**: ScottPlot 5. **3D**: HelixToolkit.Wpf (regime-cube windows; NU1701 is expected).
- **DI**: `Microsoft.Extensions.DependencyInjection`. **Logging**: Serilog → in-memory sink (the universal Activity Log pane).
- **Store**: canonical pipeline (`IMarketDataHub`/`Store`/`Ingest`/`InstrumentRegistry`) — **per-broker SQLite default** (`SqlitePerBroker`: one file per broker *per stream* — `marketdata-{broker}-bars|l1|trades|l2.db` — for parallel writers + isolation; `-l2` persists L2 depth, which the other SQLite backends drop; canonical identity stays in the shared `marketdata.db` registry), plus single-file `Sqlite`, Postgres+TimescaleDB (auto-falls-back to SQLite), or QuestDB (split: L1/L2 quotes/trades/depth → QuestDB, bars → SQLite; no silent fallback). Store reads take an optional `BrokerKind? source` (null = all brokers merged). **Archive**: Telegram offloader.
- **AI analyst**: Python sidecar (`tools/python-ml/`) behind `IAiAnalystClient` HTTP/JSON. **C++ backtester**: `tools/cpp-backtester/` (read-only reference).
- **Tests**: xUnit + FluentAssertions + NSubstitute; `[WpfFact]` for WPF-touching tests. Postgres tests self-skip without Docker.

## Solution graph (do not break)

```
App            → MarketData, Infrastructure, UI, Login, Ai, Strategies.*, Core
Login          → Core, UI, Infrastructure
Ai             → Core, UI, Infrastructure, MarketData
Strategies.*   → Infrastructure, UI, Core
Infrastructure → MarketData, Core
MarketData     → Core
UI             → Core
Core           → (nothing)
```

- **`Core` has zero deps on UI / WPF / any broker SDK.** New domain types go there.
- **`MarketData` sits below Infrastructure** — the whole canonical pipeline (hub / ingest / repository / store / archive / registry) + `IUiDispatcher`. Depends only on Core. Don't make it reference Infrastructure.
- **New broker code → `Infrastructure/<Broker>/` behind `IBrokerClient`.** View-models never see `EClientSocket` / `NTDirect` / `OpenClient` / `Alpaca.Markets`.
- **New strategy → its own `TradingTerminal.Strategies.<Name>` project.** Engine-side `IBacktestStrategy` lives in `Infrastructure/Backtest/Strategies/`; the live project wraps it via `LiveSignalStrategyViewModelBase` (in `UI`).
- **Login / AI are their own projects** so the App shell stays thin. The shell-handoff factories (`ILoginShellFactory`/`IMainShellFactory`) stay in App.

## Project map (where things live)

| Area | Project / path |
|---|---|
| Domain types, interfaces, options | `TradingTerminal.Core` (`Brokers/`, `MarketData/`, `Backtest/`, `Notifications/`, `AiAnalyst/`, `Strategies/`, `Regime/`) |
| Canonical pipeline (hub/ingest/repo/store/archive/registry, `IUiDispatcher`) | `TradingTerminal.MarketData` |
| Broker clients, backtest engine + strategies, notifications, regime, `WpfDispatcher` | `TradingTerminal.Infrastructure` |
| ViewModelBase, themes, `LiveSignalStrategyViewModelBase`, `LiveStrategyHostServices`, `InMemoryLogSink` | `TradingTerminal.UI` |
| Login window, credential store, broker login forms, `AddLogin()` | `TradingTerminal.Login` |
| AI analyst seam (`IAiAnalystClient` Null/Http), enricher, `AddAiAnalyst()` | `TradingTerminal.Ai` (shared seam only) |
| AI tool windows (one project each) — Market analyst, factor research, ML features, backtest analysis | `TradingTerminal.Ai.<Name>` (`MarketAnalyst`/`FactorResearch`/`MlFeatures`/`BacktestAnalysis`) |
| Per-strategy live windows (10) | `TradingTerminal.Strategies.<Name>` |
| Per-tool windows (one project each) — each ships its own `Add…Surface` DI extension | `TradingTerminal.<Name>` (`Charts`/`OrderBook`/`VolumeFootprint`/`Heatmap`/`Correlation`/`MarketRegime`/`InstrumentRegime`/`Backtest`/`Recording`) |
| Machine Learning menu windows (one project each) — time-series stats over historical bars; math lives in `Core/Quant/TimeSeries/` | `TradingTerminal.Ml.<Name>` (`Stationarity`/`ArimaGarch`/`KalmanFilter`) |
| Shell, MainWindow, menu, DI composition (`AppDependencyInjection`), `App.xaml.cs`, notifications + archive UI | `TradingTerminal.App` (thin shell; tools moved out) |
| Headless backtest CLI | `TradingTerminal.Backtest.Cli` (`daxalgo-backtest`) |

Live strategies (9): SigmaIcFlow (Σ⁻¹·IC Order-Flow Optimizer; engine class still `ApexScalperStrategy`), CumulativeDelta, FilteredOrderFlow, ImbalanceHeatFront, IndexKScoreSurface, IndexRegimeGraph, OrderFlowCube, OrderFlowPressureMap, OrderFlowSurfaceSpike. (Removed 2026-07-01: OrnsteinUhlenbeck, VolatilityTargeted, OrderFlowToxicity.)

**Strategy-vs-tool rule:** anything that registers an `ITradingStrategy` / `StrategyFactoryRegistration` (including multi-ticker *monitor* strategies like OrderFlowPressureMap) is a **strategy**: project `TradingTerminal.Strategies.<Name>`, namespace to match, **Strategies** solution folder, DI via `Add<Name>Strategy()` called from `AddStrategyPlugins()`. Tool projects (`Add…Surface`, Tools/Charts menu) are only for non-strategy windows. When in doubt: if it belongs in the Strategies catalog, it's a strategy project.

Per-tool projects: the App shell no longer hosts tool windows — each tool is its own `TradingTerminal.<Name>` project (flat under `src/`, grouped in the `.sln` by **Charts** / **Tools** / **AI** / **Machine Learning** / **Strategies** solution folders). App references them and opens them via `IServiceProvider`; each project ships its own `Add…Surface` extension called from `App.xaml.cs`. The Charts menu hosts Charts/OrderBook/VolumeFootprint/Heatmap; the Machine Learning menu hosts Stationarity & Differencing / ARIMA & GARCH / Kalman Filter (`TradingTerminal.Ml.<Name>`, math in `Core/Quant/TimeSeries/`).

## Architectural rules (always relevant)

1. **Strict MVVM.** No business logic in `.xaml.cs`. VMs inherit `ViewModelBase`; use `[ObservableProperty]`/`[RelayCommand]`.
2. **Strategies & brokers are plug-ins.** Adding one = new code + one DI line; the shell never references concretes. Go through `IStrategyFactory` / `IBrokerSelector`, never `new`.
3. **Threading is one-layer.** Broker callbacks marshal to the UI dispatcher inside `MarketDataRepository` (`IUiDispatcher`). No `Dispatcher.Invoke` in VMs.
4. **Streaming via `IAsyncEnumerable<Bar>/<Tick>`** with `[EnumeratorCancellation]`; cancellation is the unsubscribe path. Connection state is `IObservable<ConnectionState>` (don't poll). Reconnect backoff is wired (1s→30s).
5. **`IBrokerClient.ConnectAsync` takes no params** — each impl reads its own `IOptions<XxxOptions>`.
6. **Canonical identity is `InstrumentId`, not broker symbology.** `Quote`/`TradePrint`/`OhlcvBar` always carry `EventTimeUtc + IngestTimeUtc + Source + Sequence + EventTimeApproximate` — never strip provenance.
7. **Store writes are non-blocking** (`Enqueue*` returns immediately; batched background writer). **Ingest is tick-primary — don't write live bars to the store** (bars are aggregated downstream).
8. **Live strategy VMs subscribe to the hub, not the broker.** `LiveSignalStrategyViewModelBase` consumes `IMarketDataHub.Quotes/Bars/Depth(InstrumentId)` and starts pumps via `IMarketDataIngest.Subscribe(...)`. The `LiveStrategyHostServices` bundle (Repository + Hub + Ingest + Store + BrokerSelector + **ActivityLog**) is injected into every per-strategy VM ctor — don't add ad-hoc deps to that ctor.
9. **Universal Activity Log.** There's one app-wide log (`InMemoryLogSink`, in `UI`): Serilog feeds it as `Source="System"`; strategies/tabs append via `Log(...)` / `ActivityLog.Append(source, level, msg)` tagged by name. The MainWindow `ACTIVITY LOG` pane renders the filtered union. **Don't add per-window log panels** — route to the shared sink.
10. **Trade tape is opt-in per broker** (`SubscribeTradesAsync`). IB, Binance, and Ironbeam are wired; NT/cTrader/Alpaca throw `NotSupportedException`. Trade-tape strategies must check capability and fail loudly.
11. **Polyglot seams are subprocess + HTTP/JSON** (AI analyst, C++ backtester) — no Python/native deps in the C# build. Bind the sidecar to `127.0.0.1` only.

## Skills (lazy-loaded — invoke via the Skill tool when relevant)

| Skill | Use when |
|---|---|
| `navigator` | "Where is X?" / "which project owns Y?" — load first for orientation instead of grepping. |
| `broker-gotchas` | Editing `Infrastructure/Ib|Ninja|CTrader|Alpaca/` or diagnosing broker errors. |
| `add-broker` / `add-strategy` / `add-notifier` | Wiring a new broker / strategy / notification transport. |
| `backtest-engine` | Fee models, risk caps, fills, OMS stubs, `BacktestSession`, the CLI, the Backtest window. |
| `market-data-pipeline` | Touching `TradingTerminal.MarketData` (hub/store/ingest/registry/archive). |
| `regime-cube-strategy` | Any 3D regime-cube/surface strategy from `ideas.md`. |
| `archive-offloader` | The Telegram archive offloader. |
| `ai-analyst` | The Python sidecar, `IAiAnalystClient`, the enricher (shared seam in `TradingTerminal.Ai`); the AI market-analyst window lives in `TradingTerminal.Ai.MarketAnalyst`. |
| `wpf-mvvm-rules` | Writing/editing VMs, code-behind, threading, async/Dispatcher, XAML (the shell is plain MahApps windows — no docking framework). |
| `memory-safety` | Adding/editing any tool/chart/strategy/AI window or streaming VM — bounded channels, batch-drain, coalesced redraw, IDisposable teardown so a feed can't pile up RAM. The `leakcheck-on-stop` hook enforces a subset. |
| `software-architecture` | Planning multi-project work — decomposition, design-pattern catalog, the plan contract. The `manager` agent loads this. |
| `quant-math` | Touching OU/correlation/PCA/3D-geometry/VPIN/Markov/vol math (`Strategies.*`, Correlation). |
| `skill-author` | Adding/fixing a skill — frontmatter, "pushy" triggering, bespoke-vs-external + the licensing rule. |
| `paper-reproduction` | The Paper Lab pipeline — paper → sandboxed repro → bridge into the backtest engine as a paper-tagged strategy (`Core/Research/`, `Infrastructure/Research/`, `Ai.PaperLab`). The `paper-repro` agent loads this. |
| `untrusted-execution` | Running untrusted third-party/paper code safely — deny-by-default Docker/WSL2/VM sandbox, egress allowlist, quotas, kill-tree, never in-process. Load before touching `Infrastructure/Research/Sandbox/` or `ISandboxRunner`. |
| `paper-ingestion` | The arXiv paper→repo ingestion seam (`IPaperIngestClient` Null/Http) + the SQLite repro job/cache store (cloned from the archive manifest store). |

## Subagents & model routing

Match the cheapest model tall enough for the task. Spawn a subagent only when search breadth, parallelism, or specialized expertise justifies it — **default to the main thread**. A spawn starts cold (system prompt + AGENTS.md + skill + re-exploration), so single-project / 1–2-file changes are always done inline; the Stop hooks (`build-on-stop`, `verify-on-stop`) are the gate — no `build-runner`/`verifier` spawns for inline work. The manager → workers → build-runner → verifier spine is reserved for features spanning **3+ projects** with parallelizable parts; manager plans must embed file paths + findings so workers don't re-explore. Full rules: `.Codex/agents/README.md` ("Token discipline").

| Task shape | Route to | Model |
|---|---|---|
| Feature spanning 3+ projects → get an Execution Plan to run | `manager` (loads `software-architecture`) | Opus |
| Single-project edit in a strategy / tool / AI window | main thread inline; or `strategies` / `tool-windows` / `ai-windows` if it's heavy | Sonnet |
| Build+test gate after parallel workers (otherwise `dotnet build` inline) | `build-runner` | Haiku |
| Plan-aware review of the integrated diff (the watcher) | `verifier` | Sonnet |
| "Where is X?", project-internal lookups (3+ rounds) | `wpf-explorer` | Haiku |
| Broad cross-cutting "how does X work?" research | `Explore` (general-purpose) | Sonnet |
| Single known-path lookup | Read/Grep directly | — |
| Targeted XAML / binding / theming / window-layout fix | `xaml-fixer` | Sonnet |
| Per-strategy VM / baseline authoring (2–4 files, clear template) | main thread | Opus |
| TWS API wiring, EWrapper threading, contract/historical subtleties | `ib-api-expert` | Opus |
| Cube/surface strategy work (load `regime-cube-strategy` first) | main thread | Opus |
| Paper Lab reproduction subsystem (Core/Research + Infrastructure/Research + untrusted-code sandbox + sidecar repro) | `paper-repro` | Opus |
| Pre-commit review of a staged diff | `dotnet-reviewer` | Sonnet |
| Plan an implementation before coding | `Plan` agent | — |

**Token discipline:** don't re-read a file you just edited; prefer `Edit` over `Write`; grep before read; parallelize independent calls; don't dump huge tool results back unfiltered; don't spawn a subagent for a 30-second task. Use `/fast` for faster Opus turns.

## Build & run

```powershell
# Windows/WPF tree
dotnet build TradingTerminal.Windows.slnx
dotnet test  TradingTerminal.Windows.slnx
dotnet run --project src/windows/Shell/TradingTerminal.App

# Linux/Avalonia tree (also builds on Windows; net9.0)
dotnet build TradingTerminal.Linux.slnx
dotnet run --project src/linux/Shell/TradingTerminal.App.Avalonia
```
There is no top-level `dotnet build` with no argument anymore — two solutions exist, so always name one. `build-and-test.ps1` (Windows) and `linux/build-and-test.sh` / `linux/Dockerfile` (Linux) wrap each tree.

Defaults: IB and NT are wired purely by build-time DLL resolution (`HAS_IBAPI` from `C:\TWS API\`; `HAS_NTAPI` from `NTDirect.dll`) — there's no `UseRealClient` switch and no per-broker synthetic fallback; cTrader needs OAuth at login; Alpaca needs ApiKey+Secret (`IsLive` toggles paper/live; live stock stream pinned to IEX). No broker required to build/run — the `Simulated` broker (`BrokerKind.Simulated`, `SimulatedBrokerClient` in `Infrastructure/Simulation/`) is always registered and serves a synthetic random-walk feed or local-store replay.

### Dev launch profiles (login bypass + data-source switch)

`src/TradingTerminal.App/Properties/launchSettings.json` defines profiles selected by `DOTNET_ENVIRONMENT`, each layering an `appsettings.{Env}.json` (repo root) over `appsettings.json`. `DevOptions.BypassLogin` skips the login window and auto-connects `DevOptions.AutoConnectBrokers`; `SimulatedBrokerOptions` drives the feed.

| Profile | `DOTNET_ENVIRONMENT` | Behaviour |
|---|---|---|
| `App (Login)` | *(none)* | Normal — login window shown. |
| `Dev: Simulated (offline)` | `DevSim` | No login; `Simulated` broker, **Synthetic** random-walk. Fully offline (SQLite, no Docker/network). |
| `Dev: Replay (local DB)` | `DevReplay` | No login; `Simulated` broker, **Replay** of the local store (10× clock), synthetic fallback where no data. |
| `Dev: Live (no login)` | `DevLive` | No login; auto-connects a real broker (default IB) using saved credentials. |

Switch via the VS debug-target dropdown, or `DOTNET_ENVIRONMENT=DevSim dotnet run --project src/windows/Shell/TradingTerminal.App`. These dev files are off in the shipped build.

## What NOT to do

- Don't rename `net9.0-windows` → `net8.0-windows`. Don't put broker SDK types in `Core`/`UI`/`MarketData`.
- Don't make `Core` depend on anything, or `MarketData` depend on `Infrastructure`.
- Don't `new` strategies/brokers from the shell — go through `IStrategyFactory` / `IBrokerSelector`.
- Don't build a strategy as a tool project: no `TradingTerminal.<Name>` + `Add…Surface` for anything with an `ITradingStrategy` — it must be `TradingTerminal.Strategies.<Name>` in the Strategies solution folder (see the strategy-vs-tool rule above).
- Don't subscribe to broker streams from a VM — go through `IMarketDataHub` / `IMarketDataIngest`.
- Don't write live bars to the store (tick-primary ingest).
- Don't add per-window log panels — use the shared `InMemoryLogSink` (Activity Log).
- Don't add 2FA to the login window; don't add NuGet sources for IB (DLL resolution is wired).
- Don't add live order-execution paths — this build is data/signals only.
- Don't bind the Python sidecar to `0.0.0.0` (`127.0.0.1` only). Don't `--no-verify` commits.

## When unsure

`docs/architecture.md` has the full design rationale + key interface signatures — check it before adding new abstractions. Per-topic docs: `getting-started` · `user-guide` · `brokers` · `market-data` · `market-regime` · `strategies` · `backtesting` · `ai-analyst` · `polyglot` · `notifications` · `configuration` · `troubleshooting` · `contributing`.
