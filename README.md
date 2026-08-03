# DaxAlgo Terminal

DaxAlgo Terminal is a Windows desktop application for receiving, normalizing, recording, archiving,
visualizing, and backtesting market data and strategy signals. The public repository ships one WPF
application: `TradingTerminal.App.Basic`, targeting .NET 9.

> **Data and signals only.** DaxAlgo Terminal has no live order-execution path. A strategy can emit
> simulated orders during a backtest and signals during a live data session, but the application does
> not place orders with a broker. The disabled **Execution Engine > Not yet available** menu item makes
> this boundary explicit in the UI.

The application has no DaxAlgo product account, subscription sign-in, or entitlement gate. On a normal
start it opens the broker-selection window. Broker credentials are requested only when the selected data
source needs them.

## Repository scope

This repository contains the open-source Windows application, shared libraries, backtest engine, runtime
plugin host, SDK packages, authoring tools, and tests. It contains exactly one application shell:
`src/windows/Shell/TradingTerminal.App.Basic`.

A Professional product exists in a separate private overlay. It is not built by this repository and its
features are not part of the public application described here.

First-party strategies also live outside this repository and are built against the versioned SDK
contract. A clean clone therefore opens with an empty live catalog. Users populate it by authoring a
strategy in the application or installing a compatible `.daxplugin` runtime plugin.

## What ships

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
- An in-app AI strategy builder and agent-CLI launch workflow under **Strategy Studio**.

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
| **Settings** | Notifications; AI providers |
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
workspace. Provider selection and credentials are configured under **Settings > AI providers**.

An authored strategy becomes the same kind of runtime plugin as an externally built strategy. It can
contribute a backtest kernel, catalog metadata, and an optional live signal view. Generated or authored
code is still subject to compiler, SDK-compatibility, trust, and policy checks.

### Author an external runtime plugin

The source tree declares SDK version `0.2.0-alpha` and contains the SDK projects and authoring CLI, but
it does not contain the strategy template sources or a sample plugin. The CLI's `strategy new` action
expects the separately packaged `DaxAlgo.Templates` template.

At the time of this rewrite, the public NuGet feed contains only `0.1.x` releases of `DaxAlgo.Sdk` and
`DaxAlgo.Templates`. Those packages do not match this host: before SDK 1.0, the loader requires the same
major/minor version. The external scaffold path is therefore blocked for a public reader until matching
`0.2.x` packages are published. Use the in-app authoring path for the current host; do not retarget an
old template and assume it will load.

Once matching packages exist, the intended scaffold workflow is:

```powershell
dotnet new install DaxAlgo.Templates::0.2.0-alpha --force
dotnet new daxalgo-strategy -n MyStrategy -o MyStrategy --ui
dotnet build MyStrategy/MyStrategy.slnx
dotnet test MyStrategy/MyStrategy.slnx
```

The repository's authoring CLI wraps the same workflow after a matching template is installed:

```powershell
dotnet run --project src/windows/Tools/DaxAlgo.StrategyTool -- strategy new --name MyStrategy --output MyStrategy --ui
dotnet run --project src/windows/Tools/DaxAlgo.StrategyTool -- strategy build --project MyStrategy
dotnet run --project src/windows/Tools/DaxAlgo.StrategyTool -- strategy test --project MyStrategy
dotnet run --project src/windows/Tools/DaxAlgo.StrategyTool -- strategy package --project MyStrategy
```

Install the resulting `.daxplugin` from **Strategy Studio > Extensions**, then restart the terminal.
The manager also accepts the main plugin DLL for local development. These external commands document
the implemented workflow but were not end-to-end runnable against the current public package feed.

The runtime contract is small:

1. Put signal and backtest logic in an `IBacktestStrategy` kernel.
2. Use the supplied `IClock`; never use wall-clock time in strategy logic.
3. Send simulated orders only through `IOrderRouter`, with a unique client order ID.
4. Declare the exact `StrategyDataRequirement` flags used: L1, bars, depth, and/or trade tape.
5. Implement catalog metadata through `ITradingStrategy`. A live card also needs a compatible view-model
   and either a custom view or the host-composed view path.
6. Expose one public, parameterless `IStrategyPlugin`. Its `Register(IPluginRegistrar)` method adds the
   strategy metadata, view/view-model factory registration, and `BacktestStrategyOption` to the guarded
   service collection.
7. Keep plugin state instance-local, warm up before emitting signals, flatten simulated positions at the
   end of a run, and do not use file, network, registry, process, or reflection-emit access from the
   kernel.
8. Build and test against the published SDK packages. Do not reference host projects or package copies
   of `TradingTerminal.*` host assemblies.

`.daxplugin` is the package consumed by the current live catalog and Extensions manager. The separate
`.daxstrategy` format is an isolated-backtest bundle format; it is not a replacement name for a live
plugin and is not currently loaded into the live catalog.

The generated [strategy authoring contract](sdk/ai-context/daxalgo-strategy-context.md) contains the full
kernel API, parameter schema, worked example, and data-specific reference packs used by the in-app
builder.

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

Runtime strategy plugin
    -> DaxAlgo.Sdk / DaxAlgo.Sdk.Wpf
    -> published contracts
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
broker streams; and strategy implementations remain runtime plugins rather than shell project
references.

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
