# Project agents — routing index

A lean fleet of **19 agents**: 3 orchestration, 8 foundational (one per load-bearing
project), 3 consolidated window owners (strategies / tool windows / AI windows), and
5 specialists. Each agent knows its projects' ownership, dependency constraints (the
solution graph it must not break), conventions, and which skill to load first.

> **History:** this used to be a 39-agent per-project fleet (one agent per window).
> All 39 descriptions loaded into the system prompt on *every* request, and routine
> single-window edits got farmed out to cold-start subagents — that burned tokens for
> no benefit. The 24 single-window agents are now folded into `strategies`,
> `tool-windows`, and `ai-windows`; their per-project quirks live in tables inside
> those three agent bodies, so nothing was lost.

## Token discipline (read this before spawning anything)

A subagent spawn is the **expensive path**: it starts cold (system prompt + CLAUDE.md +
memory + agent body + skill) and re-derives context the main thread already has.

- **Default to the main thread.** Single-project or 1–2-file changes: edit inline, load
  the relevant skill yourself. No manager, no worker, no build-runner, no verifier — the
  `build-on-stop` / `verify-on-stop` hooks already gate compile errors, the layer graph,
  and SDK leaks at turn-end.
- **Spawn a worker** only when the work spans 3+ projects with parallelizable parts, when
  a large exploration would pollute the main context, or for the specialists
  (`ib-api-expert`, `wpf-explorer` for 3+-round searches).
- **The spine** (`manager` → workers → `build-runner` → `verifier`) is reserved for
  genuinely multi-project features. The manager's plan must embed file paths + findings
  so workers don't re-explore.
- **`build-runner`** only after multiple workers ran in parallel; for inline work just run
  `dotnet build` yourself. **`verifier`** only when there is a manager plan to verify against.
- **Context layer first.** Load `.claude/context/symbols.md` + `index.md` + `deps.json` for the
  module before grepping source; follow `.claude/context/PROTOCOL.md`. Hard floor: no subagent for
  <3-file changes. `build-runner` uses the narrowest `.slnf` (`TradingTerminal.Windows.Basic|Intermediate.slnf`),
  not the full `.slnx`. Never re-Read a file already read this turn — cite the task scratchpad.

## Orchestration tier

| Role | Agent | Model | What it does |
|---|---|---|---|
| Lead / architect | `manager` | opus | Read-only. Decomposes a multi-project prompt into an ordered, agent-routed **Execution Plan** with file paths + findings embedded (loads `software-architecture`). Never edits code. |
| Build gate | `build-runner` | haiku | Runs `dotnet build` + `dotnet test`, returns a distilled pass/fail report. Only needed after parallel workers. |
| Watcher / verifier | `verifier` | sonnet | Plan-aware review of the integrated diff vs the solution graph, MVVM, threading, quant math, tests. Returns a BLOCKER/NIT punch list. |

The `verify-on-stop.ps1` Stop hook is the verifier's **enforcement arm** — a deterministic
layer-graph + SDK-leak gate that *blocks turn-end* on violations. (Build errors are gated by
`build-on-stop.ps1`.) For inline work the hooks alone are the gate.

## Foundational layer
| Project | Agent | Model |
|---|---|---|
| Core | `core-domain` | opus |
| MarketData | `market-data` | opus |
| Infrastructure | `infrastructure` | opus |
| UI | `ui-shared` | sonnet |
| Login | `login` | sonnet |
| Ai (seam) | `ai-seam` | sonnet |
| App (shell) | `app-shell` | opus |
| Backtest.Cli | `backtest-cli` | sonnet |

## Consolidated window owners
| Projects | Agent | Model |
|---|---|---|
| All 12 `Strategies.*` live windows | `strategies` | sonnet |
| All 10 tool windows (Charts, OrderBook, VolumeFootprint, Heatmap, Correlation, MarketRegime, InstrumentRegime, MarkovRegime, Backtest, Recording) | `tool-windows` | sonnet |
| All 4 `Ai.*` windows (MarketAnalyst, FactorResearch, MlFeatures, BacktestAnalysis) | `ai-windows` | sonnet |

Hard 3D/quant math inside a strategy window (cube/surface geometry, OU/VPIN estimators) is
main-thread Opus work per CLAUDE.md routing — the `strategies` agent handles the window/VM
template and escalates the math.

## Specialists (not project-scoped)
`ib-api-expert` (opus) · `xaml-fixer` (sonnet) · `wpf-explorer` (haiku) · `dotnet-reviewer` (sonnet) ·
`paper-repro` (opus — the Paper Lab reproduction subsystem: `Core/Research/` + `Infrastructure/Research/`
+ the untrusted-code sandbox + sidecar repro endpoints; loads `paper-reproduction` + `untrusted-execution`)

## Orchestration & live monitoring

Prompt the main thread naming the area; it works inline or routes to the owning agent (see the
agent → skill mapping in [`../MULTI-AGENT.md`](../MULTI-AGENT.md)). For parallel work, agents
launch as background subagents.

**Watch them live** (there is no "FleetView" feature — these are the real mechanisms):
- **`claude agents`** (CLI Agent View) — every session grouped by *Needs input / Working
  / Completed*, peek or attach to any one.
- **`Ctrl+B`** — background a running subagent so several run concurrently.
- **Desktop app → Views → Tasks** — live pane of all background agents.
- **Forks** — `CLAUDE_CODE_FORK_SUBAGENT=1` for parallel forks with a fork panel.

Full workflow + env toggles: [`../MULTI-AGENT.md`](../MULTI-AGENT.md).

**Model rationale:** Opus where a mistake ripples (Core/MarketData/Infrastructure/App) or the
work is genuinely hard (IB threading); Sonnet for focused window/tool/strategy edits with a
clear template; Haiku for mechanical build/search — biasing toward fewer tokens per the
project's routing rules.
