# DaxAlgo Terminal — Claude Working Guide

A modular **multi-broker** WPF trading terminal. WPF + .NET 9. Four broker backends behind one `IBrokerClient` seam: Interactive Brokers (TWS API), NinjaTrader 8 (NTDirect P/Invoke), cTrader (Spotware Open API 2.0 over TLS+protobuf), and Alpaca (REST + WebSocket via `Alpaca.Markets` NuGet).

This file is the always-loaded core. Detail lives in **skills** (lazy-loaded by trigger) — see the skill pointer table below. Don't re-derive conventions every session.

## Stack snapshot

- **Target framework**: `net9.0-windows`. No .NET 8 SDK on the box. Don't rename to `net8.0-windows`.
- **MVVM**: `CommunityToolkit.Mvvm` — `[ObservableProperty]`, `[RelayCommand]`, source generators.
- **Shell**: MahApps Metro chrome + AvalonDock VS2013 Dark theme.
- **Charts**: ScottPlot 5.
- **DI**: `Microsoft.Extensions.DependencyInjection`.
- **Logging**: Serilog with custom in-memory sink for the Logs pane.
- **Local data store**: canonical market-data pipeline (`IMarketDataHub`/`Store`/`Ingest`/`InstrumentRegistry`) with two interchangeable backends — embedded **SQLite** (`Microsoft.Data.Sqlite`, default, zero-config) and **PostgreSQL + TimescaleDB** (`Npgsql`, via root `docker-compose.yml`). Postgres auto-falls-back to SQLite at startup if unreachable.
- **Data archive**: Telegram-backed offloader (WTelegramClient, parquet bundles split at ~1.9 GB, sha256-verified) — see `Infrastructure/MarketData/Archive/`.
- **3D visuals**: HelixToolkit.Wpf 2.27.0 for the regime-cube strategy windows (NU1701 warning is expected; works on net9.0-windows).
- **AI analyst**: Python sidecar (`tools/python-ml/daxalgo-ml.exe`) behind an HTTP+JSON seam (`IAiAnalystClient`); WPF stays hermetic.
- **C++ backtester**: in-tree at `tools/cpp-backtester/` (CMake, separate from the C# engine — read-only reference).
- **Tests**: xUnit + FluentAssertions + NSubstitute. WPF-touching tests use `[WpfFact]` (`Xunit.StaFact`). Postgres integration tests self-skip when Docker isn't running.

## Solution graph (do not break this)

```
App        → Infrastructure, UI, Strategies.*, Core
Strategies → Infrastructure, UI, Core
Infra      → Core
UI         → Core
Core       → (nothing)
```

- **`Core` has zero deps on UI / WPF / IB / NT / cTrader / Alpaca.** New domain types go there.
- **New broker code goes in `Infrastructure/<Broker>/` behind `IBrokerClient`** — view-models never see `EClientSocket` / `NTDirect` / `OpenClient` / `Alpaca.Markets` types.
- **New strategies are their own `TradingTerminal.Strategies.<Name>` project.** The engine-side `IBacktestStrategy` impl lives in `Infrastructure/Backtest/Strategies/`; the per-strategy live project wraps it via `LiveSignalStrategyViewModelBase` (in `TradingTerminal.UI`).

## Architectural rules (always relevant)

1. **Strict MVVM.** No business logic in `.xaml.cs`. View-models inherit `ViewModelBase`. Use `[ObservableProperty]` and `[RelayCommand]`.
2. **Strategies are plug-ins.** Adding a strategy = new project + one `services.AddMyStrategy()` line in `App.xaml.cs`. The shell never references strategy concretes.
3. **Brokers are plug-ins too.** Adding a broker = new `IBrokerClient` impl + DI block. Repository, view-models, strategies stay untouched.
4. **Threading is a one-layer concern.** Broker callbacks marshal to the UI dispatcher inside `MarketDataRepository`. No `Dispatcher.Invoke` in view-models.
5. **Async streaming via `IAsyncEnumerable<Bar>` / `IAsyncEnumerable<Tick>`** with `[EnumeratorCancellation]`. Cancellation is the natural unsubscribe path.
6. **Connection state is observable.** `IObservable<ConnectionState>` flows ConnectionManager → repository → view-models. Don't poll.
7. **Reconnect with exponential backoff** (already wired, 1s → 30s cap). New broker calls assume the connection can drop mid-call.
8. **`IBrokerClient.ConnectAsync` takes no params.** Each impl reads its own `IOptions<XxxOptions>`. The login form pushes user creds into options before flipping the selector.
9. **Canonical identity is `InstrumentId`, not broker symbology.** New strategies/persistence/store code keys on `InstrumentId`; the registry resolves broker `Contract`s to ids and back. `Quote`/`TradePrint`/`OhlcvBar` always carry `EventTimeUtc + IngestTimeUtc + Source + Sequence + EventTimeApproximate` — never strip provenance.
10. **Store writes are non-blocking.** `IMarketDataStore.Enqueue*` returns immediately; a background batched writer flushes on `WriteBatchSize` or `FlushIntervalMs`. Don't await disk on the ingest hot path.
11. **Live strategy VMs subscribe to the hub, not the broker.** `LiveSignalStrategyViewModelBase` consumes `IMarketDataHub.Quotes/Bars/Depth(InstrumentId)` and starts pumps via `IMarketDataIngest.Subscribe(...)`. The shared `LiveStrategyHostServices` bundle (`Repository + Hub + Ingest + Store + BrokerSelector`) is injected into every per-strategy VM ctor — don't add new ad-hoc deps to that ctor.
12. **Trade tape is opt-in per broker.** `IBrokerClient.SubscribeTradesAsync` returns `IAsyncEnumerable<TradeTick>`. Only IB is wired (via `reqTickByTickData("AllLast")`); NT / cTrader / Alpaca throw `NotSupportedException`. Trade-tape strategies must check broker capability at Continue and fail loudly.
13. **Polyglot seams are subprocess + HTTP/JSON.** AI Analyst (`tools/python-ml/`) and the C++ backtester (`tools/cpp-backtester/`) stay out-of-process — no Python or native deps leak into the C# build. See `docs/polyglot.md`.

## Skills (lazy-loaded — invoke via the Skill tool when relevant)

| Skill | Use when |
|---|---|
| `broker-gotchas` | Editing `Infrastructure/Ib/`, `/Ninja/`, `/CTrader/`, `/Alpaca/`, or diagnosing broker-specific errors (502/326/200, OAuth, AT Interface, depth events, IEX feed pinning). |
| `add-broker` | Wiring a fifth `IBrokerClient` backend (Tradovate, Rithmic, etc.). |
| `add-strategy` | Adding an engine-side `IBacktestStrategy` and/or a per-strategy live UI project (`LiveSignalStrategyViewModelBase`). |
| `add-notifier` | Adding an `INotificationTransport` (Slack/Email/etc.) alongside Telegram and Discord. |
| `backtest-engine` | Touching fee models, risk caps, fill simulation, OMS stubs, `BacktestSession`, the k-way quote/trade merge, the CLI, or the Backtest tab. |
| `market-data-pipeline` | Touching `IMarketDataHub` / `Store` / `Ingest` / `InstrumentRegistry`, adding store tables, changing ingest normalization, or wiring trade-tape into a new broker. |
| `regime-cube-strategy` | Adding any new strategy from `ideas.md` (Order Flow Cube family) — covers the 2D-with-color-before-3D rule, Helix Toolkit conventions, trade-tape capability check, and the log-panel shape used by every cube window. |
| `archive-offloader` | Touching `Infrastructure/MarketData/Archive/` — adding store tables to the bundle, changing retention, or swapping the Telegram transport for another archive backend. |
| `ai-analyst` | Touching the Python sidecar (`tools/python-ml/`), the `IAiAnalystClient` seam, the `AiAnalystEnricher`, or the AI Market Analyst dock pane. |
| `wpf-mvvm-rules` | Writing or editing view-models, code-behind, threading, async/Dispatcher, or anything XAML / AvalonDock. |

## Subagents (model-tuned, spawn when the task matches)

| Subagent | Model | Use for |
|---|---|---|
| `wpf-explorer` | Haiku | "Where is X defined?" / "which files reference Y?" — cheap reads. Spawn when a search needs 3+ rounds. |
| `xaml-fixer` | Sonnet | Targeted XAML / binding / theming / AvalonDock tweaks. |
| `ib-api-expert` | Opus | TWS API wiring, EWrapper callbacks, threading bugs in IB code, contract / historical-data subtleties. |
| `dotnet-reviewer` | Sonnet | Pre-commit review of staged changes. Catches MVVM violations, layer breaks, missing `ConfigureAwait(false)` in library code. |

**Default**: do simple work in the main thread. Spawn an `Explore` subagent for broad searches; spawn `ib-api-expert` when touching `Infrastructure/Ib/` non-trivially.

## Model routing & token discipline

Right model for the job. Don't burn Opus on grep; don't ship a Helix-Toolkit-rebuild on Haiku.

**Routing table** (match the cheapest model that's tall enough for the task):

| Task shape | Model / agent | Why |
|---|---|---|
| "Where is X defined?", "Which files reference Y?", project-internal symbol lookups (3+ rounds expected) | `wpf-explorer` (Haiku) | Read-only, narrow context — Haiku is 5–10× cheaper and just as accurate at file:line answers. |
| Broad codebase research, cross-cutting "how does X work?" spanning many files | `Explore` (general-purpose, Sonnet) | Read-window large enough to summarize; cheaper than running the main Opus thread through 20 reads. |
| Single targeted file lookup, known path | Read / Grep directly | Don't spawn a subagent for one file. |
| Targeted XAML / binding / theming / AvalonDock fix | `xaml-fixer` (Sonnet) | Small surface, well-scoped — Sonnet handles WPF idioms reliably. |
| Per-strategy live VM edits, FX/index/baseline strategy authoring | Main thread (Opus) | Touches 2–4 files with a clear template; main-thread iteration is fastest. |
| TWS API wiring, EWrapper threading, contract / historical-data subtleties | `ib-api-expert` (Opus) | "Being wrong here means silent data loss or app freezes" — pay for the strong model. |
| Cube / Surface strategy work (3D Helix, signal independence, k-way merge) | Main thread (Opus) — load `regime-cube-strategy` skill first | High-stakes math; the skill loads the conventions so we don't re-derive them. |
| Pre-commit review of staged diff | `dotnet-reviewer` (Sonnet) | Catches MVVM violations, layer breaks, missing `ConfigureAwait(false)` for less than the cost of a re-iteration. |
| Plan an implementation strategy before coding | `Plan` agent | Surfaces architectural trade-offs cheaply before the expensive write phase. |
| Claude Code / SDK / API questions | `claude-code-guide` agent | Targeted reference lookup — don't web-search from the main thread. |

**Token-discipline rules** (apply on every turn, not just complex ones):

- **Don't re-read a file you just edited.** Edit/Write would have errored if it failed; the harness tracks file state.
- **Prefer `Edit` over `Write`** for existing files — only the diff goes over the wire.
- **Grep before Read.** If you can find the answer in 5 lines of grep output, don't read 500 lines of source.
- **Parallelize independent tool calls** in a single response. Two sequential round-trips cost ~2× the tokens of one parallel batch.
- **Don't spawn a subagent for a 30-second task.** Each spawn restarts cold and re-derives context — that's the expensive path. Default to main thread; spawn only when search breadth, parallelism, or specialized expertise justifies it.
- **Don't pass huge tool results back unfiltered.** When a subagent's output is large, ask for "under 200 words" or "punch-list only".
- **Use `/fast` for Opus when you want faster output** (same Opus model, faster turn-around).

**Anti-patterns to avoid:**

- Running `grep` / `cat` / `find` / `ls` via Bash — always use the dedicated tools (Grep, Read, Glob).
- Reading a whole file when only a known line range is needed (use `offset` + `limit`).
- Re-running the same search after a tool result already answered it.
- Spawning `ib-api-expert` for a one-line typo fix in `Infrastructure/Ib/`.
- Spawning `Explore` for "find one file" — that's `Glob`.

**Tasks lists (`TaskCreate`)**: use only for multi-step work where progress tracking helps the user. Single-step asks ("read this file", "fix this typo") don't need a task list.

## Build & run

```powershell
dotnet build
dotnet test
dotnet run --project src/TradingTerminal.App
```

Build prints, when applicable:
- `IB CSharpAPI resolved from: <path>` — IB real client compiled in (`HAS_IBAPI`).
- `NTDirect resolved from: <path>` — NT real client compiled in (`HAS_NTAPI`).

cTrader is always compiled in (NuGet package; no compile-time gate).

Defaults: `InteractiveBrokers:UseRealClient: true` (standard install at `C:\TWS API\`). NinjaTrader `false` (explicit opt-in). cTrader requires OAuth credentials at the login form. Alpaca requires ApiKey + ApiSecret at the login form; `IsLive` toggles between paper and live endpoints, and the live stock stream is pinned to IEX (free plan can't subscribe to SIP).

## What NOT to do

- Don't add 2FA logic to the login window (TWS handles its own; NT relies on the running instance; cTrader uses OAuth; Alpaca uses API key + secret).
- Don't rename `net9.0-windows` to `net8.0-windows`.
- Don't put `IBApi` / `NTDirect` / cTrader proto / `Alpaca.Markets` types in `Core` or `UI`.
- Don't `new` strategies from the shell — go through `IStrategyFactory`.
- Don't `new` broker clients from the shell — go through `IBrokerSelector`.
- Don't add NuGet sources for IB — DLL resolution is already wired.
- Don't add `--no-verify` to git commits to bypass hooks.
- Don't make `Core` depend on anything.
- Don't write live bars to the store — ingest is tick-primary (commit `9757b4d`); bars are aggregated downstream.
- Don't subscribe to broker streams from a view-model — go through `IMarketDataHub` / `IMarketDataIngest`.
- Don't read parquet files directly in new code — use `IMarketDataStore`. (Existing `ParquetTickReader` call sites in AI/Research tabs are tracked for migration.)
- Don't bind the Python sidecar to `0.0.0.0` — `127.0.0.1` only. Same trust boundary as the WPF process.

## When unsure

`docs/architecture.md` has the full design rationale and key interface signatures. Check it before adding new abstractions — chances are there's already a slot to plug into.

Other docs (all under `docs/`):

- `getting-started.md` · `user-guide.md` — onboarding and tour.
- `brokers.md` — per-broker setup details (IB / NT / cTrader / Alpaca).
- `market-data.md` — canonical pipeline + Postgres/SQLite + archive.
- `market-regime.md` — composite regime score (FRED + Yahoo + Fear&Greed + AAII).
- `strategies.md` — the 25+ shipped strategy projects, grouped by family.
- `backtesting.md` — engine internals + CLI walkthrough.
- `ai-analyst.md` · `polyglot.md` — Python sidecar architecture.
- `notifications.md` — Telegram / Discord / Ollama / AI Analyst pipeline.
- `configuration.md` · `troubleshooting.md` · `contributing.md`.
