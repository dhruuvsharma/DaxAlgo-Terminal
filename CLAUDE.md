# DaxAlgo Terminal — Claude Working Guide

A modular **multi-broker** WPF trading terminal. WPF + .NET 9. Four brokers behind one `IBrokerClient` seam: Interactive Brokers (TWS API), NinjaTrader 8 (NTDirect P/Invoke), cTrader (Spotware Open API 2.0), Alpaca (REST + WebSocket). **Data/signals only — no live order execution.**

This is the always-loaded core. Detail lives in **skills** (lazy-loaded by trigger) and **docs/**. Don't re-derive conventions each session — load the matching skill, or `navigator` for "where does X live".

## Stack

- **TFM**: `net9.0-windows7.0` (in `Directory.Build.props`). Don't rename to `net8.0-windows` or strip the `7.0`.
- **MVVM**: `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`). **Shell**: MahApps Metro + AvalonDock VS2013 Dark. **Charts**: ScottPlot 5. **3D**: HelixToolkit.Wpf (regime-cube windows; NU1701 is expected).
- **DI**: `Microsoft.Extensions.DependencyInjection`. **Logging**: Serilog → in-memory sink (the universal Activity Log pane).
- **Store**: canonical pipeline (`IMarketDataHub`/`Store`/`Ingest`/`InstrumentRegistry`) — SQLite default (zero-config), Postgres+TimescaleDB (auto-falls-back to SQLite), or QuestDB (split: L1/L2 quotes/trades/depth → QuestDB, bars → SQLite; no silent fallback). **Archive**: Telegram offloader.
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
| Per-strategy live windows (9) | `TradingTerminal.Strategies.<Name>` |
| Per-tool windows (one project each) — each ships its own `Add…Surface` DI extension | `TradingTerminal.<Name>` (`Charts`/`OrderBook`/`VolumeFootprint`/`Heatmap`/`Correlation`/`MarketRegime`/`InstrumentRegime`/`MarkovRegime`/`Backtest`/`Recording`) |
| Shell, MainWindow, menu, DI composition (`AppDependencyInjection`), `App.xaml.cs`, notifications + archive UI | `TradingTerminal.App` (thin shell; tools moved out) |
| Headless backtest CLI | `TradingTerminal.Backtest.Cli` (`daxalgo-backtest`) |

Live strategies (9): ApexScalper, CumulativeDelta, ImbalanceHeatFront, IndexKScoreSurface, OrderFlowCube, OrderFlowSurfaceSpike, OrderFlowToxicity, OrnsteinUhlenbeck, VolatilityTargeted.

Per-tool projects: the App shell no longer hosts tool windows — each tool is its own `TradingTerminal.<Name>` project (flat under `src/`, grouped in the `.sln` by **Charts** / **Tools** / **AI** / **Strategies** solution folders). App references them and opens them via `IServiceProvider`; each project ships its own `Add…Surface` extension called from `App.xaml.cs`. The Charts menu hosts Charts/OrderBook/VolumeFootprint/Heatmap.

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
10. **Trade tape is opt-in per broker** (`SubscribeTradesAsync`). Only IB is wired; NT/cTrader/Alpaca throw `NotSupportedException`. Trade-tape strategies must check capability and fail loudly.
11. **Polyglot seams are subprocess + HTTP/JSON** (AI analyst, C++ backtester) — no Python/native deps in the C# build. Bind the sidecar to `127.0.0.1` only.

## Skills (lazy-loaded — invoke via the Skill tool when relevant)

| Skill | Use when |
|---|---|
| `navigator` | "Where is X?" / "which project owns Y?" — load first for orientation instead of grepping. |
| `broker-gotchas` | Editing `Infrastructure/Ib|Ninja|CTrader|Alpaca/` or diagnosing broker errors. |
| `add-broker` / `add-strategy` / `add-notifier` | Wiring a new broker / strategy / notification transport. |
| `backtest-engine` | Fee models, risk caps, fills, OMS stubs, `BacktestSession`, the CLI, the Backtest tab. |
| `market-data-pipeline` | Touching `TradingTerminal.MarketData` (hub/store/ingest/registry/archive). |
| `regime-cube-strategy` | Any 3D regime-cube/surface strategy from `ideas.md`. |
| `archive-offloader` | The Telegram archive offloader. |
| `ai-analyst` | The Python sidecar, `IAiAnalystClient`, the enricher (shared seam in `TradingTerminal.Ai`); the AI dock pane lives in `TradingTerminal.Ai.MarketAnalyst`. |
| `wpf-mvvm-rules` | Writing/editing VMs, code-behind, threading, async/Dispatcher, XAML/AvalonDock. |
| `software-architecture` | Planning multi-project work — decomposition, design-pattern catalog, the plan contract. The `manager` agent loads this. |
| `quant-math` | Touching OU/correlation/PCA/3D-geometry/VPIN/Markov/vol math (strat-*, `correlation`, `markovregime`). |
| `skill-author` | Adding/fixing a skill — frontmatter, "pushy" triggering, bespoke-vs-external + the licensing rule. |

## Subagents & model routing

Match the cheapest model tall enough for the task. Spawn a subagent only when search breadth, parallelism, or specialized expertise justifies it — default to the main thread.

| Task shape | Route to | Model |
|---|---|---|
| Plan a multi-project change → get an Execution Plan to run | `manager` (loads `software-architecture`) | Opus |
| Build+test gate after workers finish | `build-runner` | Haiku |
| Plan-aware review of the integrated diff (the watcher) | `verifier` | Sonnet |
| "Where is X?", project-internal lookups (3+ rounds) | `wpf-explorer` | Haiku |
| Broad cross-cutting "how does X work?" research | `Explore` (general-purpose) | Sonnet |
| Single known-path lookup | Read/Grep directly | — |
| Targeted XAML / binding / theming / AvalonDock fix | `xaml-fixer` | Sonnet |
| Per-strategy VM / baseline authoring (2–4 files, clear template) | main thread | Opus |
| TWS API wiring, EWrapper threading, contract/historical subtleties | `ib-api-expert` | Opus |
| Cube/surface strategy work (load `regime-cube-strategy` first) | main thread | Opus |
| Pre-commit review of a staged diff | `dotnet-reviewer` | Sonnet |
| Plan an implementation before coding | `Plan` agent | — |

**Token discipline:** don't re-read a file you just edited; prefer `Edit` over `Write`; grep before read; parallelize independent calls; don't dump huge tool results back unfiltered; don't spawn a subagent for a 30-second task. Use `/fast` for faster Opus turns.

## Build & run

```powershell
dotnet build
dotnet test
dotnet run --project src/TradingTerminal.App
```

Defaults: IB and NT are wired purely by build-time DLL resolution (`HAS_IBAPI` from `C:\TWS API\`; `HAS_NTAPI` from `NTDirect.dll`) — there's no `UseRealClient` switch and no per-broker synthetic fallback; cTrader needs OAuth at login; Alpaca needs ApiKey+Secret (`IsLive` toggles paper/live; live stock stream pinned to IEX). No broker required to build/run — the `Simulated` broker (`BrokerKind.Simulated`, `SimulatedBrokerClient` in `Infrastructure/Simulation/`) is always registered and serves a synthetic random-walk feed or local-store replay.

### Dev launch profiles (login bypass + data-source switch)

`src/TradingTerminal.App/Properties/launchSettings.json` defines profiles selected by `DOTNET_ENVIRONMENT`, each layering an `appsettings.{Env}.json` (repo root) over `appsettings.json`. `DevOptions.BypassLogin` skips the login window and auto-connects `DevOptions.AutoConnectBrokers`; `SimulatedBrokerOptions` drives the feed.

| Profile | `DOTNET_ENVIRONMENT` | Behaviour |
|---|---|---|
| `App (Login)` | *(none)* | Normal — login window shown. |
| `Dev: Simulated (offline)` | `DevSim` | No login; `Simulated` broker, **Synthetic** random-walk. Fully offline (SQLite, no Docker/network). |
| `Dev: Replay (local DB)` | `DevReplay` | No login; `Simulated` broker, **Replay** of the local store (10× clock), synthetic fallback where no data. |
| `Dev: Live (no login)` | `DevLive` | No login; auto-connects a real broker (default IB) using saved credentials. |

Switch via the VS debug-target dropdown, or `DOTNET_ENVIRONMENT=DevSim dotnet run --project src/TradingTerminal.App`. These dev files are off in the shipped build.

## What NOT to do

- Don't rename `net9.0-windows` → `net8.0-windows`. Don't put broker SDK types in `Core`/`UI`/`MarketData`.
- Don't make `Core` depend on anything, or `MarketData` depend on `Infrastructure`.
- Don't `new` strategies/brokers from the shell — go through `IStrategyFactory` / `IBrokerSelector`.
- Don't subscribe to broker streams from a VM — go through `IMarketDataHub` / `IMarketDataIngest`.
- Don't write live bars to the store (tick-primary ingest).
- Don't add per-window log panels — use the shared `InMemoryLogSink` (Activity Log).
- Don't add 2FA to the login window; don't add NuGet sources for IB (DLL resolution is wired).
- Don't add live order-execution paths — this build is data/signals only.
- Don't bind the Python sidecar to `0.0.0.0` (`127.0.0.1` only). Don't `--no-verify` commits.

## When unsure

`docs/architecture.md` has the full design rationale + key interface signatures — check it before adding new abstractions. Per-topic docs: `getting-started` · `user-guide` · `brokers` · `market-data` · `market-regime` · `strategies` · `backtesting` · `ai-analyst` · `polyglot` · `notifications` · `configuration` · `troubleshooting` · `contributing`.
