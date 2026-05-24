# DaxAlgo Terminal — Claude Working Guide

A modular **multi-broker** WPF trading terminal. WPF + .NET 9. Three broker backends behind one `IBrokerClient` seam: Interactive Brokers (TWS API), NinjaTrader 8 (NTDirect P/Invoke), and cTrader (Spotware Open API 2.0 over TLS+protobuf).

This file is the always-loaded core. Detail lives in **skills** (lazy-loaded by trigger) — see the skill pointer table below. Don't re-derive conventions every session.

## Stack snapshot

- **Target framework**: `net9.0-windows`. No .NET 8 SDK on the box. Don't rename to `net8.0-windows`.
- **MVVM**: `CommunityToolkit.Mvvm` — `[ObservableProperty]`, `[RelayCommand]`, source generators.
- **Shell**: MahApps Metro chrome + AvalonDock VS2013 Dark theme.
- **Charts**: ScottPlot 5.
- **DI**: `Microsoft.Extensions.DependencyInjection`.
- **Logging**: Serilog with custom in-memory sink for the Logs pane.
- **Local data store**: canonical market-data pipeline (`IMarketDataHub`/`Store`/`Ingest`/`InstrumentRegistry`) with two interchangeable backends — embedded **SQLite** (`Microsoft.Data.Sqlite`, default, zero-config) and **PostgreSQL + TimescaleDB** (`Npgsql`, via root `docker-compose.yml`). Postgres auto-falls-back to SQLite at startup if unreachable.
- **Tests**: xUnit + FluentAssertions + NSubstitute. WPF-touching tests use `[WpfFact]` (`Xunit.StaFact`). Postgres integration tests self-skip when Docker isn't running.

## Solution graph (do not break this)

```
App        → Infrastructure, UI, Strategies.*, Core
Strategies → Infrastructure, UI, Core
Infra      → Core
UI         → Core
Core       → (nothing)
```

- **`Core` has zero deps on UI / WPF / IB / NT / cTrader.** New domain types go there.
- **New broker code goes in `Infrastructure/<Broker>/` behind `IBrokerClient`** — view-models never see `EClientSocket` / `NTDirect` / `OpenClient`.
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

## Skills (lazy-loaded — invoke via the Skill tool when relevant)

| Skill | Use when |
|---|---|
| `broker-gotchas` | Editing `Infrastructure/Ib/`, `/Ninja/`, `/CTrader/`, or diagnosing broker-specific errors (502/326/200, OAuth, AT Interface, depth events). |
| `add-broker` | Wiring a fourth `IBrokerClient` backend. |
| `add-strategy` | Adding an engine-side `IBacktestStrategy` and/or a per-strategy live UI project. |
| `add-notifier` | Adding an `INotificationTransport` (Slack/Email/etc.) alongside Telegram and Discord. |
| `backtest-engine` | Touching fee models, risk caps, fill simulation, OMS stubs, `BacktestSession`, the CLI, or the Backtest tab. |
| `wpf-mvvm-rules` | Writing or editing view-models, code-behind, threading, async/Dispatcher, or anything XAML / AvalonDock. |

## Subagents (model-tuned, spawn when the task matches)

| Subagent | Model | Use for |
|---|---|---|
| `wpf-explorer` | Haiku | "Where is X defined?" / "which files reference Y?" — cheap reads. Spawn when a search needs 3+ rounds. |
| `xaml-fixer` | Sonnet | Targeted XAML / binding / theming / AvalonDock tweaks. |
| `ib-api-expert` | Opus | TWS API wiring, EWrapper callbacks, threading bugs in IB code, contract / historical-data subtleties. |
| `dotnet-reviewer` | Sonnet | Pre-commit review of staged changes. Catches MVVM violations, layer breaks, missing `ConfigureAwait(false)` in library code. |

**Default**: do simple work in the main thread. Spawn an `Explore` subagent for broad searches; spawn `ib-api-expert` when touching `Infrastructure/Ib/` non-trivially.

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

Defaults: `InteractiveBrokers:UseRealClient: true` (standard install at `C:\TWS API\`). NinjaTrader `false` (explicit opt-in). cTrader requires credentials at the login form.

## What NOT to do

- Don't add 2FA logic to the login window (TWS handles its own; NT relies on the running instance; cTrader uses OAuth).
- Don't rename `net9.0-windows` to `net8.0-windows`.
- Don't put `IBApi` / `NTDirect` / cTrader proto types in `Core` or `UI`.
- Don't `new` strategies from the shell — go through `IStrategyFactory`.
- Don't `new` broker clients from the shell — go through `IBrokerSelector`.
- Don't add NuGet sources for IB — DLL resolution is already wired.
- Don't add `--no-verify` to git commits to bypass hooks.
- Don't make `Core` depend on anything.

## When unsure

`docs/architecture.md` has the full design rationale and key interface signatures. Check it before adding new abstractions — chances are there's already a slot to plug into.
