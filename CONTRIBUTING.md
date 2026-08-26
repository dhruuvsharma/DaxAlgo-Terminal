# Contributing to DaxAlgo Terminal

This repository ships one Windows application, `TradingTerminal.App.Basic`, plus its shared libraries,
execution engine, sandbox strategy runtime, SDK, tools, and tests. Propose changes against what is
present here. Do not describe or depend on private-edition code.

## Prerequisites

- Windows 10 or Windows 11.
- .NET 9 SDK.
- Git.

No broker account is needed for development: the keyless public crypto feeds (Binance, Coinbase,
Bybit, Kraken, OKX, Deribit, Hyperliquid) need no credentials, and a great deal can be verified without
an account at all — response parsing is pure, and the signing schemes have published test vectors. A
new broker adapter written from documentation is marked `BrokerStatus.Unverified` until someone runs it
against a real account; please do not promote that status without having done so.
Interactive Brokers and NinjaTrader compile only when their vendor DLLs resolve locally; their absence
must not break the rest of the solution.

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

Run the narrowest relevant test project while developing, then build and test the named solution before
opening a pull request. Do not use an unqualified `dotnet build`; always name the project or solution
being checked.

```powershell
dotnet test TradingTerminal.Windows.slnx -m:1
```

`-m:1` is required rather than preferred. `dotnet test <solution>` runs one invocation per project in
parallel, and several suites touch machine-wide singletons; serialising is what makes the result
deterministic.

Documentation-only changes require tracked-link validation, spelling/terminology review, and
`git diff --check`. A product build is needed only when the change also touches generated, compiled,
or executable examples.

## Layering rules

Keep dependencies pointing inward:

| Layer | Rule |
|---|---|
| Core | Domain and contracts only; no WPF, storage implementation, application, or broker-SDK dependency |
| MarketData | Canonical ingest, identity, hub, stores, queries, and archives; remains below Infrastructure |
| Infrastructure | Concrete brokers and adapters; broker SDK types do not escape this layer |
| UI and feature surfaces | Consume Core/MarketData seams; view-models do not subscribe directly to broker SDK streams |
| App.Basic | Composition and shell behavior only; it is the sole public application shell |
| Strategies | External SDK units or runtime plugins; never add a strategy ProjectReference to the shell |

Preserve canonical `InstrumentId` identity, event provenance, tick-primary non-blocking ingest, bounded
UI streams, deterministic disposal, and strict MVVM.

### Live money

This build places real broker orders, so two rules are not negotiable:

- **Do not weaken either gate.** The app-wide trading mode must keep starting in Paper and must stay
  unpersisted; the per-broker-account acknowledgement must stay per-account. Both are required for a
  live order, and collapsing them into one switch is not a refactor — a single accidental click would
  route every account to real money.
- **Do not give a strategy a route to the OMS that bypasses its virtual book.** The book is the only
  path, and it is what makes paper and real the same code path.

## Common change types

### Broker changes

Keep the broker-neutral contract in Core and the implementation in Infrastructure. Add or update its
configuration and broker-selection form together, declare whether credentials are required in
`BrokerEditionPolicy`, and keep vendor DTOs behind the adapter. Test connection cancellation, reconnect,
subscription cleanup, canonical identity, and capability reporting.

### Strategy changes

Do not add first-party strategy implementations to the host tree. The supported SDK 0.3 authoring path
starts with `DaxAlgo.Templates` and either `daxalgo-sandbox-strategy` (`IStrategyKernel`) or
`daxalgo-sandbox-visualizer` (`IVisualizer`). Keep computation capability-scoped, declare exact data
requirements, use the supplied clock and parameters, and keep the generated direct tests green. The
`DAX3001` analyzer must remain active.

`.daxalgostrategy` and `.daxalgovisualizer` are the only package formats any edition accepts. `.daxplugin`
is retired and is rejected by name, and loose `.dll` plugins are not accepted anywhere. Do not describe
the retired `daxalgo-strategy` template or the older `DaxAlgo.StrategyTool` packaging command as the
SDK 0.3 workflow. The clean catalog must remain valid when nothing is installed.

The Extensions manager reads and verifies both formats but cannot yet install one; that work is tracked
in an issue. The catalog can display strategy and visualizer cards, but installable visualizers are not
complete. Discuss the contribution format in an issue before implementing a visualizer installer.

### UI changes

Keep code-behind limited to view concerns, commands and state in view-models, streaming collections
bounded, and timers/subscriptions disposable. A shell behavior change belongs in App.Basic; there is no
second public shell to update.

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
link to private repositories. Keep implementation guides under `docs/`, SDK/agent material under
`sdk/`, and package-specific guidance beside the package or tool it documents.

## Licensing

Contributions to the application are under [AGPL-3.0](LICENSE). The projects under `src/windows/Sdk/`
carry a separate [MIT license](src/windows/Sdk/LICENSE). The proposed exception in
[LICENSE-EXCEPTIONS.md](LICENSE-EXCEPTIONS.md) is a draft and is not in force; do not represent it as an
active permission.

For the shipping behavior and repository map, start with [README.md](README.md).
