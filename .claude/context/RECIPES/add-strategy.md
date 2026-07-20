# RECIPE — create an external strategy plugin

Windows strategies are SDK plugins maintained outside this host repository. Do not add a
`TradingTerminal.Strategies.*` project under `src/windows/` and do not add a strategy
ProjectReference to an edition shell.

1. Install the maintained template package, then scaffold outside this repository:
   `dotnet new install DaxAlgo.Templates`
   `dotnet new daxalgo-strategy -n MyStrategy [--ui]`
2. Keep the plugin's only host dependency at the SDK boundary: `DaxAlgo.Sdk` for headless plugins
   or `DaxAlgo.Sdk.Wpf` for a live WPF surface. Never reference `TradingTerminal.*` projects.
3. Put signal logic in the generated `Engine/<Name>Kernel.cs` implementation of
   `IBacktestStrategy`. Keep the descriptor and `IStrategyPlugin` registration in the generated
   plugin entry point.
4. Preserve `plugin.json`, target the exact compatible SDK version, and do not ship host DLLs.
5. For `--ui`, use the generated `LiveSignalStrategyViewModelBase`/window shape, bounded feeds,
   coalesced rendering, and deterministic teardown. Load the `memory-safety` skill first.
6. Build and test the plugin's own solution, then run `pack-plugin.ps1` and install the resulting
   `.daxplugin` through the terminal's plugin manager.

Inside this repository, use `templates/content/daxalgo-strategy/` as the canonical scaffold source
and `samples/DaxAlgo.SamplePlugin/` as the minimal headless contract test. Host changes are justified
only when the SDK, loader, template, authoring tooling, or compatibility policy itself must change.
