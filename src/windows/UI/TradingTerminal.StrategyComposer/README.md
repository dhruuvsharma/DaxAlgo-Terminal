# TradingTerminal.StrategyComposer

The **default-UI composer** for AI-authored strategies. When a strategy written in the AI Strategy
Builder ships a descriptor and a live view-model but **no view** (the recommended shape — Roslyn can't
compile XAML, and a hand-rolled code-built view is almost always worse than this), the host composes its
live window here instead of refusing it a catalog card.

## What a composed window contains

Driven entirely by the descriptor's `StrategyDataRequirement`:

| Flag | Panel | Preset |
|---|---|---|
| `Bars` | `ChartsPanel` (1-minute candles + indicators) | `ChartsPanelFeatures.Embedded` |
| `Depth` | `OrderBookPanel` (ladder + microstructure strip + heatmap) | `OrderBookPanelFeatures.Embedded` |
| `TradeTape` | `VolumeFootprintPanel` (clusters + imbalances + value area) | `VolumeFootprintPanelFeatures.Embedded` |
| `L1` only | a live quote card | — |

…all wrapped in the shared strategy chrome: `StrategySetupHost` (instrument picker + Continue),
`StrategyChromeBar` (pause / presets / CSV / snapshot / help), the Start/Stop + arm strip, and the
signal feed the kernel emits into.

Every Embedded preset keeps the panel's toolbar **off** (the strategy window owns the instrument) and
its ML forecaster **unconstructed** (`MlEnabled=false` lands via the `…EmbedOptions` constructor
argument *before* the panel view-model's first subscribe, so no model is ever trained).

## Seams

- Contract: `TradingTerminal.Core.Strategies.Authoring.IAuthoredStrategyViewComposer` (Core, UI-free).
- In-session path: `AuthoredStrategyInstaller` (Infrastructure) resolves it when the compiled strategy
  has descriptor + view-model but no view.
- Restart path: the SDK's `AuthoredPluginBootstrap` registers a `StrategyFactoryRegistration` whose
  `ViewFactory` resolves the composer from the running container.
- Registration: `AddStrategyViewComposer()` — called from every edition shell's `AddStrategyPlugins()`.
