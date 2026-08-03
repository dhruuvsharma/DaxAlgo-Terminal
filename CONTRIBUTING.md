# Contributing to DaxAlgo Terminal

This repository ships one Windows application, `TradingTerminal.App.Basic`, plus its shared libraries,
backtest engine, SDK, tools, and tests. Propose changes against what is present here. Do not describe or
depend on private-edition code.

## Prerequisites

- Windows 10 or Windows 11.
- .NET 9 SDK.
- Git.

No broker account is needed for development. Use the Simulated source for an offline session or one of
the keyless public crypto feeds for network data. Interactive Brokers and NinjaTrader compile only when
their vendor DLLs resolve locally; their absence must not break the rest of the solution.

## Build and test

From the repository root:

```powershell
dotnet restore TradingTerminal.Windows.slnx
dotnet build TradingTerminal.Windows.slnx
```

Run the application with:

```powershell
dotnet run --project src/windows/Shell/TradingTerminal.App.Basic
```

Run the narrowest relevant test project while developing, then build the named solution before opening
a pull request. Do not use an unqualified `dotnet build`; always name the project or solution being
checked.

Documentation-only changes do not require product refactoring. They still require a solution build,
link validation against `git ls-files`, spelling/terminology review, and `git diff --check`.

## Layering rules

Keep dependencies pointing inward:

| Layer | Rule |
|---|---|
| Core | Domain and contracts only; no WPF, storage implementation, application, or broker-SDK dependency |
| MarketData | Canonical ingest, identity, hub, stores, queries, and archives; remains below Infrastructure |
| Infrastructure | Concrete brokers and adapters; broker SDK types do not escape this layer |
| UI and feature surfaces | Consume Core/MarketData seams; view-models do not subscribe directly to broker SDK streams |
| App.Basic | Composition and shell behavior only; it is the sole public application shell |
| Strategies | External runtime plugins built against the published SDK; never add a strategy ProjectReference to the shell |

Preserve canonical `InstrumentId` identity, event provenance, tick-primary non-blocking ingest, bounded
UI streams, deterministic disposal, strict MVVM, and the data/signals-only boundary. A contribution must
not introduce live broker order execution.

## Common change types

### Broker changes

Keep the broker-neutral contract in Core and the implementation in Infrastructure. Add or update its
configuration and broker-selection form together, declare whether credentials are required in
`BrokerEditionPolicy`, and keep vendor DTOs behind the adapter. Test connection cancellation, reconnect,
subscription cleanup, canonical identity, and capability reporting.

### Strategy changes

Do not add first-party strategy implementations to this repository. Build strategies as runtime plugins
against the matching `DaxAlgo.Sdk` and, when needed, `DaxAlgo.Sdk.Wpf` contract version. A plugin
registers its descriptor, live view/view-model factory, and `BacktestStrategyOption` through
`IStrategyPlugin`. The clean live catalog must remain valid when no plugins are installed.

The catalog can display strategy and visualizer cards, but installable visualizers are not complete: the
card contract exists and the package/contribution format does not. Discuss that format in an issue before
implementing a visualizer installer.

### UI changes

Keep code-behind limited to view concerns, commands and state in view-models, streaming collections
bounded, and timers/subscriptions disposable. A shell behavior change belongs in App.Basic; there is no
second public shell to update.

### Generated context

When source, project references, or public symbols change, refresh generated context through the locked
manager rather than editing generated files by hand:

```powershell
powershell -File .claude/context/manage-context.ps1 sync
powershell -File .claude/context/manage-context.ps1 deep-check
```

## Proposing a change

1. Search existing issues and open one for a feature, behavioral change, or architecture decision.
2. State the user-visible problem, the public-repository scope, compatibility impact, and proposed tests.
3. Make the smallest coherent change on a branch. Do not mix cleanup or unrelated formatting into it.
4. Add focused tests and update public documentation when behavior, configuration, or contracts change.
5. Verify the relevant project/tests and `TradingTerminal.Windows.slnx`; report exactly what ran.
6. Open a pull request that links the issue and summarizes files changed, observable behavior, checks,
   risks, and deferred work.

Never include credentials, account identifiers, recorded market data, private implementation details,
or unlicensed vendor binaries in an issue or pull request.

## Documentation and links

Public documentation must describe only tracked public code. Relative links must resolve to paths
returned by `git ls-files`; an untracked local file does not exist for a clone or GitHub reader. Do not
add links to private repositories or restore the former public `docs/` tree. The retained
`docs/strategy-bundles.md` file is package documentation and must stay at that path.

## Licensing

Contributions to the application are under [AGPL-3.0](LICENSE). The projects under `src/windows/Sdk/`
carry a separate [MIT license](src/windows/Sdk/LICENSE). The proposed exception in
[LICENSE-EXCEPTIONS.md](LICENSE-EXCEPTIONS.md) is a draft and is not in force; do not represent it as an
active permission.

For the shipping behavior and repository map, start with [README.md](README.md).
