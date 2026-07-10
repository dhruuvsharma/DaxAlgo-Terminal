# context changelog — append-only session journal

Newest first. One short block per session that touched the context layer or shipped notable work.
(Separate from any repo CHANGELOG; this is for Claude-session continuity.)

## 2026-07-11 — one-click launcher
- `claude-launch.bat` (repo root): cd to repo + start Claude Code with an initial prompt that
  pre-loads index/symbols/deps.
- Same day: the PRO overlay repo got its own mirrored layer (private commit ddf4485) — submodule
  pin bumped to 7a6052f, Pro-only index/symbols/deps generated there, its CLAUDE.md points at
  BOTH layers, and it has its own claude-launch.bat pre-loading both. This public layer remains
  the authority for all shared-core modules.

## 2026-07-10 (later) — pointers + routing tightening applied (Dhruv approved "apply all")
- CLAUDE.md: context-layer section added (PROTOCOL.md is now the mandated per-change path);
  solution graph corrected — `Strategies.* → DaxAlgo.Sdk.Wpf ONLY` (ADR-0008), App no longer
  lists Strategies.*; project map splits `TradingTerminal.UI` vs `TradingTerminal.UI.Core`.
- `.claude/MULTI-AGENT.md` + `.claude/agents/README.md`: hard rules — no subagent for <3-file
  changes, context layer first, narrowest `.slnf` builds, no re-reads.
- 15 agent bodies got a per-module "Context layer first" line (build-runner: narrowest-slnf rule).
- Third pass (same day, Dhruv: "everything perfect"): remaining drift FIXED — CLAUDE.md Ai graph
  line (→ Core, Infrastructure), rule 9 `InMemoryLogSink` → UI.Core, per-tool paragraph re-scoped
  (paths under `src/windows/<Group>/`; BubbleChart/Surface Lab/ML menu = Pro shell only),
  ai-analyst skill row notes the Pro repo. Agent fleet corrected: `strategies` 12→9 (+ removed
  OrderFlowToxicity/OrnsteinUhlenbeck/VolatilityTargeted quirk rows), `tool-windows` lists the
  real 9 (+ AdvancedMarketRegime/BacktestStudio rows replace removed MarketRegime/InstrumentRegime),
  `ai-windows`/`app-shell`/`backtest-cli` descriptions reflect the open-core split, README fleet
  table rows updated. No known CLAUDE.md/agent drift remains as of 2026-07-10.

## 2026-07-10 — context layer initialized
- Phase 1 audit (`AUDIT.md`): 875 files / 103,104 LOC Windows tree; 49 files >400 LOC hold 31%;
  Core 58% / UI 69% / MarketData 47% of public types never name-mentioned in any doc/skill.
- Built: `gen-context.sh` (regenerator), `index.md` + `index/` (12 group files, ~880 rows),
  `symbols.md` + `symbols/` (~8k signature lines incl. interface members), `deps.json`,
  `modules/`, `glossary.md`, `adr/`, `RECIPES/`, `PROTOCOL.md`, `MAINTENANCE.md`, `tasks/`.
- Discoveries vs CLAUDE.md (docs drift, not corrected in CLAUDE.md yet):
  1. Strategy projects reference ONLY `DaxAlgo.Sdk.Wpf`; shells load them at runtime via
     `AddStrategyPlugins()` → `Infrastructure/Plugins/PluginLoader` (ALC). The plugin-marketplace
     refactor evidently rolled out to ALL 9 strategies, not just SigmaIcFlow.
  2. `LiveSignalStrategyViewModelBase`, `LiveStrategyHostServices`, `InMemoryLogSink` live in
     `TradingTerminal.UI.Core` (`src/windows/UI/`), not `TradingTerminal.UI`.
  3. `TradingTerminal.Ai` references Core+Infrastructure only (not UI/MarketData).
  4. `IBrokerClient` lives in `Core/MarketData/`, not `Core/Brokers/`.
  5. `DaxAlgo.Sdk.Wpf` is a zero-source facade csproj.
