# Project agents — routing index

One implementer agent per solution project (you asked for the full per-project set).
Each agent owns exactly one `src/TradingTerminal.*` project: it knows that project's
ownership, dependency constraints (the solution graph it must not break), conventions,
and which skill to load first. Give a prompt naming the area and the matching agent does
the change in its own context — keeping the main thread's context lean.

> **Token note:** all 36 descriptions load into the system prompt every session. If the
> baseline cost feels heavy, collapse the rarely-touched ones (e.g. fold the 9 `strat-*`
> into one `strategies` agent) — they're just files in this folder.

## Orchestration tier (the "company of agents")

A small org sits *above* the per-project workers. The main thread plays general contractor:
it invokes the **manager** to get a plan, dispatches the worker agents that plan names, then
runs **build-runner** and **verifier** as the gate.

| Role | Agent | Model | What it does |
|---|---|---|---|
| Lead / architect | `manager` | opus | Read-only. Decomposes a prompt into an ordered, agent-routed **Execution Plan** (loads `software-architecture`). Returns the plan — never edits code. |
| Build gate | `build-runner` | haiku | Runs `dotnet build` + `dotnet test`, returns a distilled pass/fail report. |
| Watcher / verifier | `verifier` | sonnet | Plan-aware review of the integrated diff vs the solution graph, MVVM, threading, quant math, tests. Returns a BLOCKER/NIT punch list. |

The `verify-on-stop.ps1` Stop hook is the verifier's **enforcement arm** — a deterministic
layer-graph + SDK-leak gate that *blocks turn-end* on violations the agent can't be trusted to
catch every time. (Build errors are gated by `build-on-stop.ps1`.)

## Orchestration & live monitoring

You don't pick agents by hand — prompt the main thread naming the area (or ask it to "plan this
with the manager"), and it fans the work out to the owning agent, which loads its skill (see the
per-agent → skill mapping in [`../MULTI-AGENT.md`](../MULTI-AGENT.md)). For parallel work, agents
launch as background subagents.

**Watch them live** (there is no "FleetView" feature — these are the real mechanisms):
- **`claude agents`** (CLI Agent View) — every session grouped by *Needs input / Working
  / Completed*, peek or attach to any one.
- **`Ctrl+B`** — background a running subagent so several run concurrently.
- **Desktop app → Views → Tasks** — live pane of all background agents; drag beside the
  conversation for a split view.
- **Forks** — `CLAUDE_CODE_FORK_SUBAGENT=1` for parallel forks with a fork panel.

Full workflow + env toggles: [`../MULTI-AGENT.md`](../MULTI-AGENT.md).

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

## AI tool windows
| Project | Agent | Model |
|---|---|---|
| Ai.MarketAnalyst | `ai-marketanalyst` | sonnet |
| Ai.FactorResearch | `ai-factorresearch` | sonnet |
| Ai.MlFeatures | `ai-mlfeatures` | sonnet |
| Ai.BacktestAnalysis | `ai-backtestanalysis` | sonnet |

## Tool windows
| Project | Agent | Model |
|---|---|---|
| Charts | `charts` | sonnet |
| OrderBook | `orderbook` | sonnet |
| VolumeFootprint | `volumefootprint` | sonnet |
| Heatmap | `heatmap` | sonnet |
| Correlation | `correlation` | sonnet |
| MarketRegime | `marketregime` | sonnet |
| InstrumentRegime | `instrumentregime` | sonnet |
| MarkovRegime | `markovregime` | sonnet |
| Backtest (tab) | `backtest-tool` | sonnet |
| Recording | `recording` | sonnet |

## Strategy windows
| Project | Agent | Model |
|---|---|---|
| Strategies.ApexScalper | `strat-apexscalper` | sonnet |
| Strategies.CumulativeDelta | `strat-cumulativedelta` | sonnet |
| Strategies.ImbalanceHeatFront | `strat-imbalanceheatfront` | sonnet |
| Strategies.IndexKScoreSurface | `strat-indexkscoresurface` | opus |
| Strategies.OrderFlowCube | `strat-orderflowcube` | opus |
| Strategies.OrderFlowPressureMap | `strat-orderflowpressuremap` | sonnet |
| Strategies.OrderFlowSurfaceSpike | `strat-orderflowsurfacespike` | opus |
| Strategies.OrderFlowToxicity | `strat-orderflowtoxicity` | sonnet |
| Strategies.OrnsteinUhlenbeck | `strat-ornsteinuhlenbeck` | sonnet |
| Strategies.VolatilityTargeted | `strat-volatilitytargeted` | sonnet |

## Pre-existing specialist agents (not project-scoped)
`ib-api-expert` (opus) · `xaml-fixer` (sonnet) · `wpf-explorer` (haiku) · `dotnet-reviewer` (sonnet)

**Model rationale:** Opus where a mistake ripples (Core/MarketData/Infrastructure/App) or the
work is genuinely hard (3D cube/surface strategies); Sonnet for focused window/tool/strategy
edits with a clear template — biasing toward fewer tokens per the project's routing rules.
