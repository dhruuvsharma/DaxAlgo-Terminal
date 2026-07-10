# RECIPE — add a strategy (plugin era; pairs with the `add-strategy` skill)

Strategies are SDK plugins (ADR-0008): the project references ONLY `DaxAlgo.Sdk.Wpf`.
Mirror `src/windows/Strategies/TradingTerminal.Strategies.FilteredOrderFlow/` (smallest, 630 LOC).

Checklist:
1. New project `src/windows/Strategies/TradingTerminal.Strategies.<Name>/` — csproj refs `DaxAlgo.Sdk.Wpf` only.
2. Engine math: `IBacktestStrategy` impl (self-contained in the plugin, SigmaIcFlow-style `Engine/` folder).
3. VM: `<Name>StrategyViewModel : LiveSignalStrategyViewModelBase` (ctor takes `LiveStrategyHostServices` — no extra deps).
4. Window: `<Name>StrategyWindow` (+ StrategyChromeBar binds by convention).
5. Descriptor: `ITradingStrategy` impl — set `Id`, `DisplayName`, `DataRequirement`, `BacktestStrategyId`, `ResearchPaperUrl?`.
6. Plugin: `<Name>Plugin : IStrategyPlugin` (see `symbols/Strategies.SigmaIcFlow.md` → `SigmaIcFlowPlugin`) + `Add<Name>Strategy(this IServiceCollection)`.
7. Solution: add to `TradingTerminal.Windows.slnx` (Strategies folder) AND both root `.slnf` filters.
8. Discovery: shells load via `AddStrategyPlugins()` (`Shell/TradingTerminal.App.*/Composition/AppDependencyInjection.cs:105`)
   → `Infrastructure/Plugins/PluginLoader.cs`. Check that method for how in-repo strategies reach the
   loader (registration list vs plugin-dir scan) and mirror the existing 9 — do not invent a mechanism.
9. Never: a tool project (`Add…Surface`) for anything with `ITradingStrategy`; ad-hoc ctor deps; broker subscribe from the VM (hub only).

Build: `dotnet build TradingTerminal.Windows.Intermediate.slnf` · Test: `dotnet test tests/TradingTerminal.Tests.Headless --filter "FullyQualifiedName~<Name>"`
Load `memory-safety` before writing the streaming VM. Update: catalog docs, `index/Strategies.md`, `deps.json`, issue tick.
