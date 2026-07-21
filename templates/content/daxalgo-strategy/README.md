# DaxNewStrategy

A DaxAlgo Terminal strategy scaffolded by `dotnet new daxalgo-strategy`. Its EMA-cross demo kernel lives
in a deterministic, WPF-free Engine project. Replace the math there and keep the contract: that exact
engine assembly is the single implementation used by live replay and backtesting.

The default scaffold is headless. Pass `--ui` to add a live WPF window. Presentation depends on Engine;
Engine never depends on presentation or WPF.

## The loop

```powershell
dotnet build DaxNewStrategy.slnx    # build engine, Windows adapter, and tests
dotnet test DaxNewStrategy.slnx     # offline kernel harness
./pack-strategy.ps1                 # deterministic .daxstrategy (requires daxalgo-bundle)
./pack-plugin.ps1                   # legacy .daxplugin for current hosts
```

The `.daxstrategy` path can be inspected and cryptographically verified offline now. Runtime installation
arrives in the next integration slice. Current Terminal builds continue to install `.daxplugin` through
Plugins → Manage strategy plugins, followed by a restart.

## Layout

| Path | Purpose |
|---|---|
| `DaxNewStrategy/Engine/DaxNewStrategy.Engine.csproj` | Deterministic, WPF-free canonical engine project |
| `DaxNewStrategy/Engine/DaxNewStrategyKernel.cs` | The one `IBacktestStrategy` signal implementation |
| `DaxNewStrategy/Engine/DaxNewStrategyFactory.cs` | Manifest-named parameter schema, data requirements, and activation point |
| `DaxNewStrategy/DaxNewStrategy.csproj` | Windows presentation and legacy plugin adapter; references Engine |
| `DaxNewStrategy/DaxNewStrategyPlugin.cs` | Legacy registration and catalog descriptor |
| `DaxNewStrategy/plugin.json` | Legacy host manifest and bundle metadata source |
| `DaxNewStrategy/DaxNewStrategyViewModel.cs` | `--ui` only: live view-model |
| `DaxNewStrategy/DaxNewStrategyWindow.xaml` | `--ui` only: WPF view |
| `DaxNewStrategy.Tests/` | Offline tests that reference Engine directly |
| `pack-strategy.ps1` | Builds and packs the new `.daxstrategy` artifact |
| `pack-plugin.ps1` | Builds and packs the legacy `.daxplugin` adapter |

## Rules that matter

- The SDK is pre-1.0. Keep both SDK PackageReferences and `plugin.json.targetSdkVersion` aligned with the
  host SDK.
- Replace `plugin.json.publisherId` with your stable lowercase publisher/marketplace identity; do not
  derive it from the editable display-name field. Declare required bundle capabilities in the same file.
- Engine-private managed dependencies copied to Engine's build output are included automatically. Native,
  mixed-mode, host-owned, or UI-dependent libraries are rejected by the bundle verifier.
- Never reference host projects directly or ship `TradingTerminal.*` / `DaxAlgo.Sdk*` DLLs. The host
  provides those contracts.
- Strategy math, parameters, and deterministic state belong only in Engine. UI code may display or route
  engine output but must not reimplement signals.
- Declare tunables and data requirements once on the engine factory. Live replay, a single backtest, and
  optimizer sweeps all create the same kernel through that parameter-aware factory.
- Engine must not reference the Windows project, WPF, or UI assemblies. The bundle verifier checks this
  from PE metadata without loading the code.
- No file, network, process, registry, native, or dynamic-code access in strategy code.

`CLAUDE.md` and `AGENTS.md` provide the same boundary to AI coding agents.
