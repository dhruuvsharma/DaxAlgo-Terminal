---
name: navigator
description: Route DaxAlgo Terminal work to the correct Windows or Linux project, context shard, and specialist skill. Use first for codebase orientation, ownership questions, locating a feature, choosing a build target, or deciding whether a change belongs in Core, Pipeline, Shell, UI, AI, Charts, Tools, SDK, templates, or an external strategy plugin.
---

# Navigator

Route before reading source. Load `.claude/context/index.md`, `symbols.md`, and `deps.json`, then grep
the matching generated shard. For Linux/Avalonia work, use `.claude/context/linux/`; Windows and Linux
are independent trees and matching type names are not evidence of matching behavior.

## Windows routes

| Area | Path | Owns |
|---|---|---|
| Domain and seams | `src/windows/Core/TradingTerminal.Core/` | Domain records, contracts, options, quant, research, strategy and backtest seams |
| Broker and service implementations | `src/windows/Pipeline/TradingTerminal.Infrastructure/` | Brokers, notifications, research runners, composition below shells |
| Canonical market data | `src/windows/Pipeline/TradingTerminal.MarketData/` | Hub, ingest, repository, stores, discovery, archive |
| Edition shells | `src/windows/Shell/TradingTerminal.App.Basic/`, `TradingTerminal.App.Intermediate/` | Composition roots, menus, window hosting |
| Login | `src/windows/Shell/TradingTerminal.Login/` | Credentialed login forms and broker selection |
| Shared Windows UI | `src/windows/Shell/TradingTerminal.UI/`, `src/windows/UI/TradingTerminal.UI.Core/` | Themes, shell UI and reusable strategy-window support |
| Settings and authoring | `src/windows/UI/TradingTerminal.Settings/`, `TradingTerminal.StrategyComposer/` | Settings and strategy-composer UI |
| AI seam | `src/windows/AI/TradingTerminal.Ai/` | AI analyst clients/enricher only; no Windows AI tool-window projects |
| Charts | `src/windows/Charts/` | Charts, heatmap, order book and volume footprint |
| Tools | `src/windows/Tools/` | Backtest surfaces, recording, correlation, regimes, Codegen and StrategyTool |
| Backtest engine | `src/windows/Backtest/TradingTerminal.Backtest.Engine/` | Event replay, kernels, optimization and reports |
| Plugin SDK | `src/windows/Sdk/`, `templates/`, `samples/` | Public plugin contracts, canonical template and minimal sample |

First-party Windows strategies are not projects in this repository. They are external runtime plugins.
Flat pre-grouped Windows paths and in-tree Windows strategy-project paths are stale.

## Dependency direction

Keep `Core` dependency-free. Keep `MarketData` below `Infrastructure`. Broker SDK types stay in
Infrastructure. Shells compose factories and selectors; view-models consume broker-neutral seams.
Tool and chart projects do not reference sibling tools. Strategy plugins reference only the published
`DaxAlgo.Sdk` or `DaxAlgo.Sdk.Wpf` package, never host projects.

## Route by task

- Add a broker: load `add-broker` and `broker-gotchas`.
- Change market-data flow or storage: load `market-data-pipeline`; add `archive-offloader` for archive work.
- Author a strategy: load `add-strategy`; create an external SDK plugin, not an in-tree strategy project.
- Change backtesting: load `backtest-engine` and `quant-math` as needed.
- Work on research reproduction: load `paper-reproduction`, `paper-ingestion`, and
  `untrusted-execution`. The Windows backend is under Core/Pipeline Research; PaperLab UI is Linux-only.
- Change WPF lifetime or binding behavior: load `wpf-mvvm-rules` and `memory-safety`.
- Change architecture or boundaries: load `software-architecture`.

## Build routing

- Windows edition-local: `TradingTerminal.Windows.Basic.slnf` or
  `TradingTerminal.Windows.Intermediate.slnf`.
- Windows shared signature/cross-edition: `TradingTerminal.Windows.slnx`.
- Linux only when explicitly in scope: `TradingTerminal.Linux.slnx`.
- External strategy plugin: build and test its scaffold solution, then run its `pack-plugin.ps1`.

Open source only after the context shard identifies the symbol and line. Never infer one tree's
implementation from the other.
