# DaxAlgo Terminal

> **AI-native trading terminal. Vibe code your strategies.**

DaxAlgo Terminal is a Windows workspace for market data, strategy authoring, and order execution.
Describe a strategy in **Hyperion**, launch an agent CLI from **Vibe Code**, or scaffold a
capability-scoped strategy with the **DaxAlgo SDK**. The public repository ships one .NET 9 WPF
application: `TradingTerminal.App.Basic`.

```text
strategy idea
    -> Hyperion, an agent CLI, or the DaxAlgo SDK
    -> compile + tests + capability checks
    -> review, register, and run against its own virtual book
```

The application has no DaxAlgo product account, subscription sign-in, or entitlement gate. On a normal
start it opens the broker-selection window. Broker credentials are requested only when the selected data
source needs them.

## Repository scope

This repository contains the public Windows application, shared libraries, the execution engine, the
sandbox strategy runtime, runtime plugin host, SDK packages, authoring tools, templates, samples, and
tests. It contains exactly one shell: `src/windows/Shell/TradingTerminal.App.Basic`.

A Professional product exists in a separate private repository. It is not built by this repository and
its features are not part of the public application described here.

First-party strategies also live outside this repository and are built against the versioned SDK
contract. A clean clone therefore opens with an empty catalog. You populate it by authoring a strategy
in the application.

## Order execution

**This build can place real broker orders.** The execution engine, OMS, risk engine, and the
Interactive Brokers / cTrader / Alpaca execution adapters live in `src/windows/Execution/` and are
composed by the shell. Two independent gates gate live money, and **both** are required:

1. **Trading mode** — an app-wide Paper/Real switch, toggled from the broker login window. Arming Real
   requires typing the word `LIVE`. It always starts in Paper and is deliberately never persisted, so a
   stale setting cannot arm real money after an update or a machine handover. Disarming is one click and
   is never refused.
2. **Per-account acknowledgement** — a separate confirmation stored per broker account. An adapter
   refuses a live order for an account that has none.

In **Paper**, an order is recorded in the ledger and shown in the Execution Console, and never leaves
the process.

### The virtual book is the only route

A strategy never reaches the OMS directly. It trades its **own** virtual book, and the execution engine
copies that book outward. That is what makes paper and real the same code path: a strategy cannot tell
which mode it is in, because it only ever writes to its own wallet.

### Order types

Market, limit, stop, and stop-limit orders all reach the venue, and all three execution adapters map
them in both directions.

A strategy can also arm a **resting entry** — the four familiar pending orders — which is mirrored to
the broker as a genuine resting order rather than watched locally:

| Pending order | How a strategy asks for it |
|---|---|
| Buy limit | positive target, `VirtualEntryKind.Limit`, trigger below the market |
| Sell limit | negative target, `VirtualEntryKind.Limit`, trigger above the market |
| Buy stop | positive target, `VirtualEntryKind.Stop`, trigger above the market |
| Sell stop | negative target, `VirtualEntryKind.Stop`, trigger below the market |

A trigger placed on the side that would fire immediately is refused rather than quietly converted into
a market order.

## What ships

- **Vibe Code** with Hyperion and agent-CLI launch workflows for AI-assisted strategy authoring.
- The execution engine and Execution Console, behind the two gates above.
- Published DaxAlgo SDK 0.3 packages, sandbox strategy/visualizer templates, analyzer policy, and samples.
- Full broker selection across keyless and credentialed data sources.
- A broker-neutral market-data pipeline with canonical instrument identity, live fan-out, persistence,
  replay/query support, and configurable archives.
- A background market-data recorder, opened from the **REC** chip in the header. It has no menu entry.
- An **Extensions** manager that reads and verifies `.daxalgostrategy` / `.daxalgovisualizer` packages.
- A catalog UI that can represent both strategies and visualizers.
- Built-in themes, Theme Studio, notifications settings, archive controls, and a universal Activity Log.

### Not in this build

- **No backtest engine.** It was archived on 2026-08-17 and nothing replaces it yet, so there is no
  Backtest Studio and no quick-backtest action. Strategy authoring, registration, and live execution do
  not depend on it.
- **Installing** a `.daxalgostrategy` / `.daxalgovisualizer` package. The Extensions manager reads and
  verifies a package and reports what is inside, but cannot yet install one. Author strategies in the
  application instead.
- **No connectable Simulated broker.** The in-process synthetic/replay source was removed on
  2026-08-16. `BrokerKind.Simulated` survives only as a provenance tag on stored rows, because the enum
  is persisted by ordinal and removing a member would silently re-label existing history.

## Build and run

### Requirements

- Windows 10 or Windows 11.
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).
- Git.

No broker account is required: seven public crypto sources need no account or API key.

### Clone, build, and start

```powershell
git clone https://github.com/dhruuvsharma/DaxAlgo-Terminal.git
cd DaxAlgo-Terminal
dotnet build TradingTerminal.Windows.slnx
dotnet run --project src/windows/Shell/TradingTerminal.App.Basic
```

Run the tests with `-m:1`. It is required, not a preference: `dotnet test <solution>` runs one
invocation per project in parallel, and several suites touch machine-wide singletons.

```powershell
dotnet test TradingTerminal.Windows.slnx -m:1
```

The solution enables Windows targeting and writes Windows development outputs beneath
`C:\DaxAlgoBuild` by default. Override the `DaxAlgoBuildRoot` MSBuild property if that location is not
suitable.

At startup, select one or more brokers. The keyless crypto sources use public network feeds and need no
credentials. Credentialed sources require their normal broker setup.

## Brokers

`TradingTerminal.App.Basic` calls both `AddKeylessBrokers()` and `AddCredentialedBrokers()`. The broker
policy exposes both groups in the selector.

| Group | Sources | Requirement |
|---|---|---|
| Keyless | Binance, Coinbase, Bybit, Kraken, OKX, Deribit, Hyperliquid | Public crypto market data; no account or API key |
| Credentialed | Interactive Brokers | Signed-in TWS or IB Gateway; the client compiles when `CSharpAPI.dll` is available |
| Credentialed | NinjaTrader | Running NinjaTrader with its integration enabled; the client compiles when `NTDirect.dll` is available |
| Credentialed | cTrader | cTrader application credentials/access token and account selection |
| Credentialed | Alpaca | API key and secret; paper or live endpoint by configuration |
| Credentialed | Ironbeam | Username and API key; demo or live endpoint by configuration |
| Credentialed | London Strategic Edge | Provider API key |
| Credentialed | Upstox | App credentials and OAuth access token |
| Credentialed | Tradier | Access token; free sandbox token issued immediately, or production |
| Credentialed | OANDA | v20 personal access token plus an account id; practice or live |
| Credentialed | Binance, Coinbase, Bybit, Kraken, OKX, Deribit | Optional API key. These venues appear **twice** — the keyless row above serves the same market data with no account. A key adds a higher rate-limit budget and the private endpoints, and is checked against the venue the moment it is entered |

Interactive Brokers and NinjaTrader are build-time optional because their vendor DLLs are not committed.
The other clients above build from NuGet or repository source.

Interactive Brokers, cTrader, and Alpaca also have **execution** adapters. The remaining sources are
market data only.

**Verification status is published, not implied.** `BrokerCatalog` carries a status per venue, and the
login list shows it. Eight of the sixteen clients — Coinbase, Bybit, Kraken, OKX, Deribit, Hyperliquid,
Tradier and OANDA — are marked **Unverified**: written from each venue's published API documentation
and not yet run against a funded account. They are wired, tested against captured payloads and
published signing vectors, and may still differ from the live service in ways only an account reveals.
A further 31 venues are catalogued as **Planned** with no adapter yet, and cannot be selected.

Broker configuration is read from `appsettings.json`, with optional per-user values in the git-ignored
`appsettings.local.json`. Do not commit credentials.

## The current shell

The menu bar in `src/windows/Shell/TradingTerminal.App.Basic/MainWindow.xaml` is:

| Menu | Items |
|---|---|
| **File** | Reconnect to broker; Start QuestDB; Exit |
| **View** | Activity log; Theme › Customize theme (Theme Studio) |
| **Vibe Code** | Hyperion; Launch CLI; Extensions |
| **Data** | Market data archive; Archive history; Instant offload (all pending) |
| **Execution Engine** | Execution Console |
| **Settings** | Notifications |
| **Help** | Support the developer; About DaxAlgo Terminal |

The **REC** chip sits in the header rather than a menu. Its indicator lights while the background
recorder is capturing selected L1, L2, bar, or trade-tape streams.

## Catalog, strategies, and visualizers

The main catalog is intentionally empty in a clean installation. It aggregates runtime registrations
instead of linking strategy projects into the shell.

The catalog card contract has two kinds:

| Kind | Spine | Primary action |
|---|---|---|
| Strategy | Purple | Open |
| Visualizer | Blue | Add to chart |

Installable visualizers are **in progress**. The descriptor, card kind, styling, filtering, and
**Add to chart** action contract exist, but the package format is not yet installable. Do not treat a
visualizer card as evidence of a working visualizer marketplace.

> **Known gap.** A strategy earns a catalog card only when it ships its own live window. A strategy that
> supplies just a descriptor and a view-model registers successfully but does not appear in the catalog,
> so write a view for now.
>
> This is being solved by having Hyperion design and write the window from the same prompt that produces
> the strategy, rather than by the host composing one from a fixed panel map — see
> [issue #42](https://github.com/dhruuvsharma/DaxAlgo-Terminal/issues/42).

### Author a strategy in the application

Open **Vibe Code › Hyperion** to describe, generate, compile, review, and register a strategy.
**Vibe Code › Launch CLI** opens an installed supported agent CLI in an authoring workspace with the
host contract and task brief already prepared.

Generated or authored code is subject to compiler, SDK-compatibility, trust, and policy checks. A
strategy that P/Invokes never compiles, so it can never be registered. Pressing **Compile & Register**
is the consent for running it.

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
| Sandbox strategy | `IStrategyKernel` plus direct tests; authored against SDK 0.3 |
| Sandbox visualizer | `IVisualizer` plus direct tests; public runtime support covers in-memory visualizer testing |
| `.daxalgostrategy` | Open strategy package: source, UI, and assets in one file. Read, verified, and installed by **Extensions** |
| `.daxalgovisualizer` | The visualizer counterpart of the same open format |
| `.daxplugin` | **Retired 2026-08-24** — the install path is gone, not just discouraged. Rejected by name, with a message telling you to repackage |

Loose `.dll` plugins are no longer accepted in any edition.

## Market data, storage, and archives

Every broker implements the `IBrokerClient` seam. Downstream code receives normalized records identified
by canonical `InstrumentId` values rather than broker SDK types.

```text
Broker clients
    -> broker selector and connection manager
    -> normalized ingest (quotes, trades, bars, depth)
    -> bounded live hub -> UI, recorder, strategies
    -> selected store -> replay, query, archive
```

The selectable persistence providers are:

| Provider | Behavior |
|---|---|
| `QuestDb` | **Default.** Every stream — quotes, trades, depth and bars — in QuestDB. Unreachable storage is disabled rather than silently redirected somewhere you did not choose. Installed builds bundle the runtime and start it themselves, with no Docker; from source, stage it once with `scripts/stage-questdb.ps1` |
| `SqlitePerBroker` | The no-server fallback. One embedded database per broker per stream, plus a shared identity registry. Zero-config and works offline |

The recorder captures chosen streams in the background. The archive subsystem can package normalized
quotes, bars, trades, and depth as Parquet data, track archive history, and process pending offloads. It
is disabled by default and configured from the **Data** menu.

## Architecture and project map

Dependencies point inward:

```text
TradingTerminal.App.Basic
    -> feature surfaces (Execution, Recording, Settings, Login, UI)
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
| `src/windows/Execution` | OMS ledger, risk engine, lease/fencing, the secured named-pipe service, broker execution adapters, and the Execution Console |
| `src/windows/Sandbox` | The DAXQ-free model-portfolio simulator and the sandbox strategy runtime |
| `src/windows/Charts` | Chart, order-book, and volume-footprint surfaces |
| `src/windows/Shell/TradingTerminal.UI` | Shared WPF controls, catalog presentation, and strategy UI seams |
| `src/windows/Shell/TradingTerminal.Login` | Broker-selection and broker-credential forms |
| `src/windows/Shell/TradingTerminal.App.Basic` | The only public application composition root and main window |
| `src/windows/UI` | Settings surfaces, shared view-model base types, and the strategy view composer |
| `src/windows/Tools` | Strategy authoring, packaging, and recorder surfaces |
| `src/windows/Sdk` | Published plugin, package, and strategy-bundle SDK projects |

The main rules are: Core has no application-specific dependencies; MarketData stays below
Infrastructure; broker SDK types stay inside Infrastructure; view-models consume market-data seams, not
broker streams; a strategy never reaches the OMS except through its virtual book; and strategy
implementations never become shell project references.

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
