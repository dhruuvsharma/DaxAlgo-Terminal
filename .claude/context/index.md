# index — shared-core Windows map

Load this with `symbols.md` and `deps.json` before a Windows change, then grep the matching detail
file instead of scanning source. The independent Linux layer is under `linux/` and is loaded only
for Linux work.

```powershell
rg -i "footprint" .claude/context/index/
rg -n "StrategyComposer" .claude/context/index/ .claude/context/symbols/
powershell -File .claude/context/manage-context.ps1 summary
powershell -File .claude/context/manage-context.ps1 check
```

## Generated groups

The Windows layer currently covers 911 `.cs`/`.xaml`/`.axaml` files across 28 active, test, and
sample projects. Generated rows have the shape `File | LOC | Tree | Project | Ed | Pub | Purpose`.

| Detail file | Rows | Ownership |
|---|---:|---|
| `index/Core.md` | 248 | Domain, contracts, quant, ML, research |
| `index/Pipeline.md` | 159 | Infrastructure and MarketData |
| `index/Shell.md` | 203 | Basic/Intermediate shells, Login, Windows UI |
| `index/Charts.md` | 35 | Charts, OrderBook, VolumeFootprint, Heatmap |
| `index/Tools.md` | 64 | Codegen/CLI, regime, backtest, correlation, recording |
| `index/UI.md` | 41 | UI.Core, Settings, StrategyComposer |
| `index/Backtest.md` | 31 | Backtest.Engine |
| `index/AI.md` | 4 | Shared AI analyst seam |
| `index/Sdk.md` | 4 | DaxAlgo SDK (`Sdk.Wpf` is a zero-source facade) |
| `index/Samples.md` | 1 | Sample runtime plugin |
| `index/Tests.md` | 121 | Windows headless and WPF tests |

## Current topology

- High-blast-radius foundations: Core, Infrastructure, MarketData, UI.Core, and Windows UI.
- Edition shells are independent: `App.Basic` and `App.Intermediate`; ordinary work builds one
  matching `.slnf`, while cross-edition or foundation changes build `TradingTerminal.Windows.slnx`.
- Windows first-party strategies moved to the external strategies repository on 2026-07-19.
  This tree contains the SDK, sample/template, plugin loader, StrategyComposer, and authoring tools;
  a stale `Strategies.*` Windows path is an error.
- `DaxAlgo.Codegen`, `DaxAlgo.StrategyTool`, and `TradingTerminal.StrategyComposer` are active.
- Top-level `tools/`, templates, and web assets are cold/on-demand areas; query the workspace
  manifest or `rg --files` in that one root. Never read bundled/minified vendor JavaScript whole.

## Other tree

For Avalonia/Linux work, load `.claude/context/linux/index.md`, `symbols.md`, and `deps.json`.
Its module identities are tree-qualified conceptually because many names intentionally match the
Windows tree. Never use a Windows signature as evidence for Linux behavior.

Regenerate Windows mechanical detail with Git Bash:
`& "$env:ProgramFiles\Git\bin\bash.exe" .claude/context/gen-context.sh`.
Use `--check` for a non-mutating freshness test.
