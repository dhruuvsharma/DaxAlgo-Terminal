# DaxAlgo Terminal

> **AI-native trading terminal. Vibe code your strategies.**

DaxAlgo Terminal is a Windows workspace for market data, strategy research, and backtesting. Describe
a strategy in **Hyperion**, launch Codex or Claude Code from **Vibe Code**, or scaffold a capability-
scoped strategy with the **DaxAlgo SDK**. The public repository ships one .NET 9 WPF application:
`TradingTerminal.App.Basic`.

```text
strategy idea
    -> Hyperion, an agent CLI, or the DaxAlgo SDK
    -> compile + tests + capability checks
    -> review and run in a compatible host
```

> **The shipped build places no broker orders.** Since 2026-08-12 the execution engine, OMS, risk
> engine, and the Interactive Brokers / cTrader / Alpaca execution adapters live in this repository
> under `src/windows/Execution/` — but the Basic shell **composes none of them**: no execution service
> is registered, no order-entry surface is exposed, and the disabled **Execution Engine > Not yet
> available** menu item still marks the boundary in the UI. A strategy emits simulated orders during a
> backtest and signals during a live data session. The source to build a live order path is now here
> and AGPL-licensed; wiring it into a shell is deliberately left undone.

The application has no DaxAlgo product account, subscription sign-in, or entitlement gate. On a normal
start it opens the broker-selection window. Broker credentials are requested only when the selected data
source needs them.

## Repository scope

This repository contains the public Windows application, shared libraries, backtest engine, runtime
plugin host, SDK packages, authoring tools, templates, samples, and tests. It contains exactly one shell:
`src/windows/Shell/TradingTerminal.App.Basic`.

A Professional product exists in a separate private overlay. It is not built by this repository and its
features are not part of the public application described here.

First-party strategies also live outside this repository and are built against the versioned SDK
contract. A clean clone therefore opens with an empty live catalog. Users populate it by authoring a
strategy in the application or installing a compatible `.daxplugin` runtime plugin.

## What ships

- **Strategy Studio** with Hyperion and agent-CLI launch workflows for AI-assisted strategy authoring.
- Published DaxAlgo SDK 0.3 packages, sandbox strategy/visualizer templates, analyzer policy, and samples.
- Full broker selection across keyless and credentialed data sources.
- A broker-neutral market-data pipeline with canonical instrument identity, live fan-out, persistence,
  replay/query support, and configurable archives.
- A background market-data recorder, opened from the **REC** chip in the header. It has no menu entry.
- Backtest Studio under **Backtest Engine**, plus **Quick backtest (last 1 year)** on strategy-card
  context menus.
- A runtime strategy-plugin loader and **Extensions** manager for install, update, enable/disable,
  quarantine, and removal workflows.
- A catalog UI that can represent both strategies and visualizers.
- Built-in themes, Theme Studio, notifications settings, archive controls, and a universal Activity Log.

## Build and run

### Requirements

- Windows 10 or Windows 11.
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).
- Git.

No broker account is required. The in-process **Simulated** source works offline, and the five public
crypto sources need no account or API key.

### Clone, build, and start

```powershell
git clone https://github.com/dhruuvsharma/DaxAlgo-Terminal.git
cd DaxAlgo-Terminal
dotnet build TradingTerminal.Windows.slnx
dotnet run --project src/windows/Shell/TradingTerminal.App.Basic
```

The solution enables Windows targeting and writes Windows development outputs beneath
`C:\DaxAlgoBuild` by default. Override the `DaxAlgoBuildRoot` MSBuild property if that location is not
suitable.

At startup, select one or more brokers. Choose **Simulated** for a fully offline session. The keyless
crypto sources use public network feeds. Credentialed sources require their normal broker setup.

## Brokers

`TradingTerminal.App.Basic` calls both `AddKeylessBrokers()` and `AddCredentialedBrokers()`. The broker
policy exposes both groups in the selector.

| Group | Sources | Requirement |
|---|---|---|
| Keyless | Binance, Coinbase, Bybit, Kraken, OKX | Public crypto market data; no account or API key |
| Offline | Simulated | No account, key, SDK, or network |
| Credentialed | Interactive Brokers | Signed-in TWS or IB Gateway; the client compiles when `CSharpAPI.dll` is available |
| Credentialed | NinjaTrader | Running NinjaTrader with its integration enabled; the client compiles when `NTDirect.dll` is available |
| Credentialed | cTrader | cTrader application credentials/access token and account selection |
| Credentialed | Alpaca | API key and secret; paper or live data endpoint by configuration |
| Credentialed | Ironbeam | Username and API key; demo or live endpoint by configuration |
| Credentialed | London Strategic Edge | Provider API key |
| Credentialed | Upstox | App credentials and OAuth access token |

Interactive Brokers and NinjaTrader are build-time optional because their vendor DLLs are not committed.
The other clients above build from NuGet or repository source. Regardless of a broker's own live/paper
terminology, DaxAlgo consumes its data only and never routes live orders.

Broker configuration is read from `appsettings.json`, with optional per-user values in the git-ignored
`appsettings.local.json`. Do not commit credentials.

## The current shell

The menu bar in `TradingTerminal.App.Basic/MainWindow.xaml` is:

| Menu | Items |
|---|---|
| **File** | Reconnect to broker; Start QuestDB; Exit |
| **View** | Activity log; Theme; Customize theme (Theme Studio) |
| **Backtest Engine** | Backtest Studio |
| **Strategy Studio** | Vibe Code > Hyperion; Launch CLI; Extensions |
| **Data** | Market data archive; Archive history; Instant offload (all pending) |
| **Execution Engine** | Not yet available (disabled; data/signals-only boundary) |
| **Settings** | Notifications |
| **Help** | Support the developer; About DaxAlgo Terminal |

The **REC** chip sits in the header rather than a menu. Its indicator lights while the background
recorder is capturing selected L1, L2, bar, or trade-tape streams.

## Catalog, strategies, and visualizers

The main catalog is intentionally empty in a clean installation. It aggregates runtime registrations
instead of linking strategy projects into the shell.

The catalog card contract has two kinds:

| Kind | Spine | Primary action | Quick backtest |
|---|---|---|---|
| Strategy | Purple | Open | Available from the context menu |
| Visualizer | Blue | Add to chart | Not shown |

Installable visualizers are **in progress**. The descriptor, card kind, styling, filtering, and
**Add to chart** action contract exist, but there is no visualizer package/contribution format yet.
Do not treat a visualizer card as evidence of a working visualizer marketplace.

### Author a strategy in the application

Open **Strategy Studio > Vibe Code > Hyperion** to describe, generate, compile, review, and install a
strategy. **Strategy Studio > Launch CLI** opens an installed supported agent CLI in an authoring
workspace with the host contract and task brief already prepared.

An authored strategy becomes the same kind of runtime plugin as an externally built strategy. It can
contribute a backtest kernel, catalog metadata, and an optional live signal view. Generated or authored
code is still subject to compiler, SDK-compatibility, trust, and policy checks.

### Vibe code with the DaxAlgo SDK 0.3

SDK 0.3 is published on NuGet and ships two supported templates. A sandbox strategy implements
`IStrategyKernel`; a sandbox visualizer implements `IVisualizer`. Both receive only scoped market data,
a deterministic clock, typed parameters, mediated alerts, and—for strategies—a virtual model book.
The SDK carries the `DAX3001` analyzer, and every scaffold includes a direct unit test.

Create and verify a strategy:

```powershell
dotnet new install DaxAlgo.Templates::0.3.0 --force
dotnet new daxalgo-sandbox-strategy -n MyStrategy -o MyStrategy
dotnet build MyStrategy/DaxSandboxStrategy.slnx -c Release
dotnet test MyStrategy/DaxSandboxStrategy.slnx -c Release --no-build
```

Or create a data-only visualizer:

```powershell
dotnet new daxalgo-sandbox-visualizer -n MyVisualizer -o MyVisualizer
dotnet build MyVisualizer/DaxSandboxVisualizer.slnx -c Release
dotnet test MyVisualizer/DaxSandboxVisualizer.slnx -c Release --no-build
```

The scaffolded `AGENTS.md` and `CLAUDE.md` give coding agents the same capability contract as a human
author. See the [sandbox authoring guide](docs/sandbox-authoring.md) and the build-verified
[`DaxAlgo.Sandbox.Samples`](samples/DaxAlgo.Sandbox.Samples/) project for the complete API and examples.
Older examples using `dotnet new daxalgo-strategy` describe a retired scaffold and are not the SDK 0.3
workflow.

### Know the artifact boundary

| Artifact | Current role |
|---|---|
| Sandbox strategy | `IStrategyKernel` plus direct tests; authored against SDK 0.3 for a compatible product host |
| Sandbox visualizer | `IVisualizer` plus direct tests; public runtime support is available for in-memory visualizer testing |
| `.daxplugin` | Legacy live-catalog package accepted by **Strategy Studio > Extensions**; the SDK 0.3 sandbox templates do not emit it |
| `.daxstrategy` | Signed, inspectable bundle for immutable storage and isolated backtests; not the live-catalog format |

The public repository supports authoring and unit-testing sandbox units. Product hosting, strategy runs,
and backtests remain host-owned. The public application has no live broker-order execution path.

## Backtesting

Backtest Studio is available even when the live catalog is empty. Its registry includes the engine's
built-in mean-reversion kernel and three demonstration strategy options: buy-and-hold, mean reversion,
and Donchian breakout. Runtime plugins can add further `BacktestStrategyOption` registrations.

Backtests run against simulated execution: deterministic clocks, an order router, fee/fill/risk models,
historical or synthetic feeds, results, optimization, and playback. They do not create a broker order.

For an installed strategy card, **Quick backtest (last 1 year)** opens a focused run for the card's
declared backtest strategy. Visualizer cards do not show this action.

## Market data, storage, and archives

Every broker implements the `IBrokerClient` seam. Downstream code receives normalized records identified
by canonical `InstrumentId` values rather than broker SDK types.

```text
Broker clients
    -> broker selector and connection manager
    -> normalized ingest (quotes, trades, bars, depth)
    -> bounded live hub -> UI, recorder, strategies
    -> selected store -> replay, query, backtest, archive
```

The selectable persistence providers are:

| Provider | Behavior |
|---|---|
| `SqlitePerBroker` | Default. One time-series database per broker plus a shared identity registry |
| `Sqlite` | One embedded database |
| `Postgres` | PostgreSQL/TimescaleDB; falls back to SQLite if unreachable at startup |
| `QuestDb` | Quotes, trades, and depth in QuestDB with bars in SQLite; unreachable tick storage is disabled rather than silently redirected |

The recorder captures chosen streams in the background. The archive subsystem can package normalized
quotes, bars, trades, and depth as Parquet data, track archive history, and process pending offloads. It
is disabled by default and configured from the **Data** menu.

## Architecture and project map

Dependencies point inward:

```text
TradingTerminal.App.Basic
    -> feature surfaces (Backtest, BacktestStudio, Recording, Settings, Login, UI)
    -> Infrastructure
    -> MarketData
    -> Core

Sandbox strategy or visualizer
    -> DaxAlgo.Sdk 0.3
    -> capability-scoped contracts + analyzer
```

Key directories:

| Path | Responsibility |
|---|---|
| `src/windows/Core/TradingTerminal.Core` | Domain records and broker-, UI-, and storage-neutral contracts |
| `src/windows/Pipeline/TradingTerminal.MarketData` | Ingest, fan-out, persistence, registry, archive, and query pipeline |
| `src/windows/Pipeline/TradingTerminal.Infrastructure` | Broker clients, plugin loading, notifications, and concrete adapters |
| `src/windows/Backtest` | Backtest protocol, client, engine, and isolated worker |
| `src/windows/Shell/TradingTerminal.UI` | Shared WPF controls, catalog presentation, and strategy UI seams |
| `src/windows/Shell/TradingTerminal.Login` | Broker-selection and broker-credential forms |
| `src/windows/Shell/TradingTerminal.App.Basic` | The only public application composition root and main window |
| `src/windows/Tools` | Strategy authoring, packaging, Backtest Studio, quick backtest, and recorder surfaces |
| `src/windows/Sdk` | Published plugin and strategy-bundle SDK projects |

The main rules are: Core has no application-specific dependencies; MarketData stays below
Infrastructure; broker SDK types stay inside Infrastructure; view-models consume market-data seams, not
broker streams; and strategy implementations never become shell project references.

## Credits and attribution

DaxAlgo Terminal is created and maintained by [Dhruv Sharma](https://github.com/dhruuvsharma).

The project builds on .NET/WPF and open-source work including
[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet),
[MahApps.Metro](https://github.com/MahApps/MahApps.Metro),
[ScottPlot](https://github.com/ScottPlot/ScottPlot),
[Reactive Extensions](https://github.com/dotnet/reactive), and
[Serilog](https://github.com/serilog/serilog). Each dependency remains governed by its upstream license.
Broker names and marks belong to their respective owners and identify compatible integrations only;
see the [broker asset attribution](assets/brokers/README.md). No endorsement is implied.

## License

The repository is licensed under [GNU AGPL-3.0](LICENSE).

The projects under `src/windows/Sdk/` carry the [MIT license](src/windows/Sdk/LICENSE), including the
`DaxAlgo.Sdk`, `DaxAlgo.Sdk.Wpf`, and `DaxAlgo.Strategy.Bundle` package metadata. Those packages expose or
link to contracts from the AGPL host. The proposed plugin linking exception in
[LICENSE-EXCEPTIONS.md](LICENSE-EXCEPTIONS.md) is explicitly a draft and is **not in force**. Do not rely
on it as permission to distribute a closed-source plugin. This is a statement of the repository's
current license files, not legal advice.

See [CONTRIBUTING.md](CONTRIBUTING.md) for the change workflow and [CHANGELOG.md](CHANGELOG.md) for release
history.
