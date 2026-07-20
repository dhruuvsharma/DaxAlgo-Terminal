---
name: add-strategy
description: Create or modify a DaxAlgo Windows strategy as an external runtime plugin using DaxAlgo.Sdk, DaxAlgo.Sdk.Wpf, the daxalgo-strategy template, StrategyTool, plugin.json, offline tests, and .daxplugin packaging. Use when asked to add, scaffold, implement, package, test, or install a strategy. Do not create in-tree strategy projects or add strategy references to the public host.
---

# Add a Strategy Plugin

Strategies are external Windows plugins. Keep strategy code outside this repository unless the task is
explicitly about the SDK, template, sample, loader, StrategyComposer, Codegen, or StrategyTool.

## Start from the maintained template

```powershell
dotnet new install DaxAlgo.Templates
dotnet new daxalgo-strategy -n MyStrategy          # backtest/headless
dotnet new daxalgo-strategy -n MyStrategy --ui     # add a WPF live window
```

The equivalent developer CLI is:

```text
daxalgo strategy new --name MyStrategy [--ui] [--output <dir>]
daxalgo strategy build|test|package --project <dir>
```

Use `templates/content/daxalgo-strategy/` as the canonical shape and
`samples/DaxAlgo.SamplePlugin/` only as a minimal loader example. Read `docs/plugin-authoring.md` for
manifest, permissions, compatibility, signing, and submission details.

## Implement one kernel

- Put signal and state logic in `Engine/<Name>Kernel.cs` as `IBacktestStrategy`.
- Use only `IClock` for time and `IOrderRouter` for orders.
- Keep mutable state per instance; use unique client order IDs; warm up before trading; flatten in
  `OnEndAsync` when the strategy contract requires it.
- Override depth/trade handlers only when needed and declare matching `DataRequirement`s.
- Do not access host internals, broker SDKs, credentials, files, processes, or the network from the
  kernel. Declared exceptional permissions are security-reviewed.

Both live and backtest modes must run the same kernel. Never duplicate strategy math in a view-model.

## Register through the SDK

Expose one `IStrategyPlugin`. Register the descriptor and `BacktestStrategyOption` through its guarded
`IPluginRegistrar`. A `--ui` plugin may also register `StrategyFactoryRegistration` and a
`LiveSignalStrategyViewModelBase` window via `DaxAlgo.Sdk.Wpf`.

Keep `plugin.json` aligned with code:

- stable unique plugin/strategy IDs;
- exact target SDK compatibility;
- data requirements and permissions;
- correct entry assembly/type and integrity metadata.

Reference only the published SDK package. Never add a `TradingTerminal.*` project reference, ship host
DLLs, edit an edition shell, or add the plugin to a host solution.

## Verify and package

```powershell
dotnet build MyStrategy.slnx
dotnet test MyStrategy.slnx
./pack-plugin.ps1
```

Use synthetic ticks/depth/trades in the offline harness. Cover warm-up, deterministic order IDs,
cancellation/end behavior, declared data requirements, and at least one no-signal path. Install the
resulting `.daxplugin` through the Plugin Manager; loader failures must remain classified and visible.

## Host-side work

When the request changes authoring infrastructure rather than a strategy:

- contracts/loader: `src/windows/Sdk/` and the appropriate Core strategy seam;
- template: `templates/content/daxalgo-strategy/`;
- CLI/AI authoring: `src/windows/Tools/DaxAlgo.StrategyTool/` and `DaxAlgo.Codegen/`;
- visual authoring: `src/windows/UI/TradingTerminal.StrategyComposer/`;
- minimal example: `samples/DaxAlgo.SamplePlugin/`.

Treat those as platform changes with wider blast radius; keep plugin implementation out of the host.
